using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Services;

namespace OrderProcessingApp.Controllers;

[ApiController]
[Route("api/upload")]
public class UploadController : ControllerBase
{
    private static readonly string[] OrderDateFormats =
    {
        "yyyy-MM-dd",
        "dd/MM/yyyy",
        "MM/dd/yyyy",
        "dd-MM-yyyy",
        "M/d/yyyy",
        "d/M/yyyy",
        "yyyy/MM/dd"
    };

    private readonly IOrderService _orderService;
    private readonly IPendingCsvImportService _pendingCsvImportService;
    private readonly ILogger<UploadController> _logger;

    public UploadController(IOrderService orderService, IPendingCsvImportService pendingCsvImportService, ILogger<UploadController> logger)
    {
        _orderService = orderService;
        _pendingCsvImportService = pendingCsvImportService;
        _logger = logger;
    }

    [HttpPost("csv")]
    public async Task<ActionResult<CsvUploadResultDto>> UploadCsv(
        List<IFormFile> files,
        [FromQuery] bool allowDuplicates = false,
        [FromQuery] bool createMissingProducts = false,
        CancellationToken cancellationToken = default)
    {
        if (files is null || files.Count == 0 || files.All(f => f.Length == 0))
            return BadRequest(new { message = "At least one non-empty CSV file is required." });

        var aggregate = new CsvUploadResultDto();
        var pendingRows = new List<CsvOrderRowDto>();

        foreach (var file in files.Where(f => f.Length > 0))
        {
            var parseResult = await ParseCsvFileAsync(file, cancellationToken);
            aggregate.TotalRows += parseResult.TotalRows;
            aggregate.SkippedOrders += parseResult.SkippedRows;
            aggregate.ValidationErrors.AddRange(parseResult.Errors);

            if (parseResult.Rows.Count == 0)
                continue;

            var processingResult = await _orderService.CreateOrdersFromCsvRowsAsync(parseResult.Rows, allowDuplicates, createMissingProducts, cancellationToken);
            var result = processingResult.Result;
            pendingRows.AddRange(processingResult.PendingRows);

            aggregate.CreatedOrders += result.CreatedOrders;
            aggregate.SkippedOrders += result.SkippedOrders;
            aggregate.UpdatedOrders += result.UpdatedOrders;
            aggregate.FlaggedOrders += result.FlaggedOrders;
            aggregate.RequiresUserAction = aggregate.RequiresUserAction || result.RequiresUserAction;
            aggregate.Errors.AddRange(result.Errors);
            aggregate.ValidationErrors.AddRange(result.ValidationErrors);
            foreach (var missingDistributionCentre in result.MissingDistributionCentres)
            {
                if (!aggregate.MissingDistributionCentres.Contains(missingDistributionCentre, StringComparer.OrdinalIgnoreCase))
                {
                    aggregate.MissingDistributionCentres.Add(missingDistributionCentre);
                }
            }
            foreach (var missingProduct in result.MissingProducts)
            {
                if (!aggregate.MissingProducts.Contains(missingProduct, StringComparer.OrdinalIgnoreCase))
                {
                    aggregate.MissingProducts.Add(missingProduct);
                }
            }
            aggregate.Success = aggregate.Success && result.Success;
            aggregate.Type ??= result.Type;
            aggregate.Message ??= result.Message;

            _logger.LogInformation(
                "CSV upload processed file {FileName}. Parsed rows: {ParsedRows}, skipped rows: {SkippedRows}, created orders: {CreatedOrders}, validation errors: {ValidationErrorCount}",
                file.FileName,
                parseResult.TotalRows,
                parseResult.SkippedRows,
                result.CreatedOrders,
                parseResult.Errors.Count + result.ValidationErrors.Count);
        }

        if (pendingRows.Count > 0)
        {
            aggregate.FileId = await _pendingCsvImportService.SaveAsync(null, pendingRows, allowDuplicates, aggregate.MissingDistributionCentres, aggregate.MissingProducts, cancellationToken);
            aggregate.RequiresUserAction = aggregate.MissingDistributionCentres.Count > 0 || aggregate.MissingProducts.Count > 0;
        }

        return Ok(aggregate);
    }

    private async Task<ParseCsvFileResult> ParseCsvFileAsync(
        IFormFile file,
        CancellationToken cancellationToken)
    {
        var rows = new List<CsvOrderRowDto>();
        var errors = new List<CsvUploadErrorDto>();
        using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream, leaveOpen: true);

        var totalRows = 0;
        var skippedRows = 0;
        var firstNonEmptyLine = await ReadFirstNonEmptyLineAsync(reader, cancellationToken);
        if (firstNonEmptyLine is null)
        {
            errors.Add(new CsvUploadErrorDto
            {
                FileName = file.FileName,
                Message = "The file is empty or does not contain a header row."
            });

            return new ParseCsvFileResult(rows, errors, totalRows, skippedRows);
        }

        var delimiter = DetectDelimiter(firstNonEmptyLine);
        _logger.LogInformation("Detected CSV delimiter '{Delimiter}' for file {FileName}", delimiter, file.FileName);

        stream.Seek(0, SeekOrigin.Begin);
        reader.DiscardBufferedData();

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            IgnoreBlankLines = true,
            TrimOptions = TrimOptions.Trim,
            BadDataFound = null,
            MissingFieldFound = null,
            HeaderValidated = null,
            DetectColumnCountChanges = false,
            Mode = CsvMode.RFC4180,
            Delimiter = delimiter.ToString()
        };

        using var csv = new CsvReader(reader, config);

        try
        {
            if (!await csv.ReadAsync())
            {
                errors.Add(new CsvUploadErrorDto
                {
                    FileName = file.FileName,
                    Message = "The file is empty or does not contain a header row."
                });

                return new ParseCsvFileResult(rows, errors, totalRows, skippedRows);
            }

            csv.ReadHeader();
        }
        catch (Exception exception)
        {
            errors.Add(new CsvUploadErrorDto
            {
                FileName = file.FileName,
                Message = $"Unable to read CSV header: {exception.Message}"
            });

            _logger.LogWarning(exception, "CSV import failed to read header for {FileName}", file.FileName);
            return new ParseCsvFileResult(rows, errors, totalRows, skippedRows);
        }

        var headerRecord = csv.HeaderRecord ?? Array.Empty<string>();
        var headerMap = BuildHeaderMap(headerRecord);

        if (headerMap.MissingRequiredColumns.Count > 0)
        {
            foreach (var missingColumn in headerMap.MissingRequiredColumns)
            {
                errors.Add(new CsvUploadErrorDto
                {
                    FileName = file.FileName,
                    Field = missingColumn,
                    Message = $"Missing required column: {missingColumn}"
                });
            }

            return new ParseCsvFileResult(rows, errors, totalRows, skippedRows);
        }

        while (await csv.ReadAsync())
        {
            totalRows++;
            var parser = csv.Context.Parser;
            var rowNumber = parser?.RawRow ?? totalRows + 1;

            try
            {
                var values = parser?.Record ?? Array.Empty<string>();
                var rawRowText = parser?.RawRecord ?? string.Empty;
                var rowError = TryParseRow(file.FileName, rowNumber, values, rawRowText, headerMap, errors, _logger, out var row);
                if (rowError is not null)
                {
                    skippedRows++;
                    errors.Add(new CsvUploadErrorDto
                    {
                        FileName = file.FileName,
                        RowNumber = rowNumber,
                        Field = rowError.Field,
                        Message = rowError.Message
                    });

                    _logger.LogWarning("CSV import skipped line {LineNumber} in {FileName}: {Message}", rowNumber, file.FileName, rowError.Message);
                    continue;
                }

                var vendor = row!.Metadata.TryGetValue("Vendor", out var vendorValue) ? vendorValue : string.Empty;
                var depot = row.Metadata.TryGetValue("Depot", out var depotValue) ? depotValue : string.Empty;
                var destId = row.Metadata.TryGetValue("DestID", out var destIdValue) ? destIdValue : string.Empty;
                var destDesc = row.Metadata.TryGetValue("DestDesc", out var destDescValue) ? destDescValue : string.Empty;

                _logger.LogInformation(
                    "[CSV DC RAW] Vendor: {Vendor}, Depot: {Depot}, DestID: {DestID}, DestDesc: {DestDesc}",
                    vendor,
                    depot,
                    destId,
                    destDesc);
                _logger.LogInformation(
                    "[CSV DC RESOLVED] ChosenDCName: {ChosenDCName}, ChosenDCId: {ChosenDCId}",
                    row.DistributionCentre,
                    string.IsNullOrWhiteSpace(destId) ? null : destId);

                rows.Add(row!);
            }
            catch (Exception exception)
            {
                skippedRows++;
                errors.Add(new CsvUploadErrorDto
                {
                    FileName = file.FileName,
                    RowNumber = rowNumber,
                    Message = $"Unexpected parsing error: {exception.Message}"
                });

                _logger.LogWarning(exception, "CSV import failed to parse line {LineNumber} in {FileName}", rowNumber, file.FileName);
            }
        }

        return new ParseCsvFileResult(rows, errors, totalRows, skippedRows);
    }

    private static async Task<string?> ReadFirstNonEmptyLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                return null;

            if (!string.IsNullOrWhiteSpace(line))
                return line;
        }
    }

    private static char DetectDelimiter(string headerLine)
    {
        return headerLine.Contains('|') ? '|' : ',';
    }

    private static string CleanValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var normalized = Regex.Replace(value, @"[\uFEFF\u200B\u200C\u200D]", string.Empty);
        normalized = Regex.Replace(normalized, @"\s+", " ");
        return normalized.Trim().Trim('"', '\'', '“', '”').Trim();
    }

    private static RowValidationError? TryParseRow(
        string fileName,
        int lineNumber,
        IReadOnlyList<string> values,
        string rawRowText,
        CsvHeaderMap headerMap,
        List<CsvUploadErrorDto> errors,
        ILogger<UploadController> logger,
        out CsvOrderRowDto? row)
    {
        row = null;

        var orderNumber = GetMappedValue(values, headerMap.OrderNumberIndex);
        var orderDateText = GetMappedValue(values, headerMap.OrderDateIndex);
        var deliveryDateText = GetMappedValue(values, headerMap.DeliveryDateIndex);
        var distributionCentreFromGenericColumn = GetMappedValue(values, headerMap.DistributionCentreIndex);
        var destId = GetMappedValue(values, headerMap.DestIdIndex);
        var destDesc = GetMappedValue(values, headerMap.DestDescIndex);
        var vendor = GetMappedValue(values, headerMap.VendorIndex);
        var depot = GetMappedValue(values, headerMap.DepotIndex);
        var distributionCentre = !string.IsNullOrWhiteSpace(destDesc)
            ? destDesc
            : !string.IsNullOrWhiteSpace(distributionCentreFromGenericColumn)
                ? distributionCentreFromGenericColumn
                : destId;
        var productCode = GetMappedValue(values, headerMap.ProductCodeIndex);
        var productName = GetMappedValue(values, headerMap.ProductNameIndex);
        var product = GetMappedValue(values, headerMap.ProductIndex);
        var quantityText = GetMappedValue(values, headerMap.QuantityIndex);
        var costPerText = GetMappedValue(values, headerMap.CostPerIndex);
        var grossCstText = GetMappedValue(values, headerMap.GrossCostIndex);
        var exetendCstText = GetMappedValue(values, headerMap.ExetendCostIndex);
        var metadata = BuildMetadata(values, headerMap);

        logger.LogInformation(
            "[CSV FIELD INDEX] RowNumber: {RowNumber}, RawRowText: {RawRowText}, QtyIndex: {QtyIndex}, CostperIndex: {CostperIndex}, GrossCstIndex: {GrossCstIndex}, ExetendCstIndex: {ExetendCstIndex}",
            lineNumber,
            rawRowText,
            headerMap.QuantityIndex,
            headerMap.CostPerIndex,
            headerMap.GrossCostIndex,
            headerMap.ExetendCostIndex);

        logger.LogInformation(
            "[CSV PARSED VALUES] RowNumber: {RowNumber}, RawRowText: {RawRowText}, Qty raw: {QtyRaw}, Costper raw: {CostperRaw}, GrossCst raw: {GrossCstRaw}, ExetendCst raw: {ExetendCstRaw}",
            lineNumber,
            rawRowText,
            quantityText,
            costPerText,
            grossCstText,
            exetendCstText);
        var resolvedProduct = !string.IsNullOrWhiteSpace(productCode)
            ? productCode
            : !string.IsNullOrWhiteSpace(productName)
                ? productName
                : product;

        if (string.IsNullOrWhiteSpace(orderNumber))
            return new RowValidationError("OrderNumber", "Order number is required.");

        if (string.IsNullOrWhiteSpace(orderDateText))
            return new RowValidationError("OrderDate", "Order date is required.");

        if (string.IsNullOrWhiteSpace(distributionCentre))
            return new RowValidationError("DistributionCentre", "Distribution centre is required.");

        if (string.IsNullOrWhiteSpace(resolvedProduct))
            return new RowValidationError("Product", "Product is required.");

        if (string.IsNullOrWhiteSpace(quantityText))
            return new RowValidationError("Quantity", "Quantity is required.");

        if (string.IsNullOrWhiteSpace(costPerText))
            return new RowValidationError("Costper", "Costper is required");

        if (!TryParseDate(orderDateText, out var orderDate))
            return new RowValidationError("OrderDate", $"Invalid date: '{orderDateText}'");

        DateTime deliveryDate;
        if (string.IsNullOrWhiteSpace(deliveryDateText))
        {
            deliveryDate = orderDate;
        }
        else if (!TryParseDate(deliveryDateText, out deliveryDate))
        {
            return new RowValidationError("DeliveryDate", $"Invalid date: '{deliveryDateText}'");
        }

        var quantity = SafeParseDecimal(quantityText, errors, fileName, lineNumber, "Quantity");
        var quantityParsed = quantity is not null;
        var costperParsed = TryParseDecimal(costPerText, out var parsedCostPer);

        if (quantityParsed && !costperParsed)
        {
            logger.LogWarning(
                "[CSV NUMERIC MAP] RowNumber: {RowNumber}, RawRowText: {RawRowText}, Qty raw: {QtyRaw}, Costper raw: {CostperRaw}, Parsed quantity: {ParsedQuantity}, Parsed price: (invalid), Message: Costper parse failure while Qty parsed.",
                lineNumber,
                rawRowText,
                quantityText,
                costPerText,
                quantity);
        }

        if (!quantityParsed && costperParsed)
        {
            logger.LogWarning(
                "[CSV NUMERIC MAP] RowNumber: {RowNumber}, RawRowText: {RawRowText}, Qty raw: {QtyRaw}, Costper raw: {CostperRaw}, Parsed quantity: (invalid), Parsed price: {ParsedPrice}, Message: Qty parse failure while Costper parsed.",
                lineNumber,
                rawRowText,
                quantityText,
                costPerText,
                parsedCostPer);
        }

        if (quantity is null)
            return new RowValidationError("Quantity", $"Invalid number: '{quantityText}'");

        if (!costperParsed)
            return new RowValidationError("Costper", $"Invalid unit price '{costPerText}' at row {lineNumber}");

        var price = parsedCostPer;

        if (price <= 0)
            return new RowValidationError("Costper", "Costper must be greater than 0");

        var supplierTotalRaw = !string.IsNullOrWhiteSpace(grossCstText)
            ? grossCstText
            : exetendCstText;

        metadata["GrossCstRaw"] = grossCstText;
        metadata["ExetendCstRaw"] = exetendCstText;

        if (!string.IsNullOrWhiteSpace(grossCstText))
        {
            metadata["GrossCst"] = grossCstText;
        }

        if (!string.IsNullOrWhiteSpace(exetendCstText))
        {
            metadata["ExetendCst"] = exetendCstText;
        }

        if (!string.IsNullOrWhiteSpace(supplierTotalRaw))
        {
            metadata["SupplierLineTotalRaw"] = supplierTotalRaw;
            if (TryParseDecimal(supplierTotalRaw, out var supplierLineTotal))
            {
                metadata["SupplierLineTotal"] = supplierLineTotal.ToString(CultureInfo.InvariantCulture);
            }
        }

        metadata["QtyRaw"] = quantityText;
        metadata["CostperRaw"] = costPerText;

        if (TryParseDecimal(quantityText, out var qtyRawParsed)
            && TryParseDecimal(costPerText, out var costRawParsed)
            && quantity.Value == costRawParsed
            && price == qtyRawParsed
            && qtyRawParsed != costRawParsed)
        {
            logger.LogWarning(
                "[CSV NUMERIC MAP] RowNumber: {RowNumber}, RawRowText: {RawRowText}, Qty raw: {QtyRaw}, Costper raw: {CostperRaw}, Parsed quantity: {ParsedQuantity}, Parsed price: {ParsedPrice}, Message: Qty/Costper appear swapped.",
                lineNumber,
                rawRowText,
                quantityText,
                costPerText,
                quantity.Value,
                price);
        }

        logger.LogInformation(
            "[CSV PRICE MAP] SKU: {Sku}, Qty raw value: {QtyRaw}, Costper raw value: {CostperRaw}, GrossCst raw value: {GrossCstRaw}, Parsed quantity: {ParsedQuantity}, Parsed price: {ParsedPrice}",
            resolvedProduct,
            quantityText,
            costPerText,
            grossCstText,
            quantity.Value,
            price);

        logger.LogInformation(
            "[CSV RAW NUMBERS] SKU: {Sku}, QtyRaw: {QtyRaw}, CostperRaw: {CostperRaw}, GrossCstRaw: {GrossCstRaw}, ExetendCstRaw: {ExetendCstRaw}, ResolvedQuantity: {ResolvedQuantity}, ResolvedUnitPrice: {ResolvedUnitPrice}, LineTotal: {LineTotal}",
            resolvedProduct,
            metadata.TryGetValue("QtyRaw", out var qtyRawForNumbers) ? qtyRawForNumbers : string.Empty,
            metadata.TryGetValue("CostperRaw", out var costperRawForNumbers) ? costperRawForNumbers : string.Empty,
            metadata.TryGetValue("GrossCstRaw", out var grossRawForNumbers) ? grossRawForNumbers : string.Empty,
            metadata.TryGetValue("ExetendCstRaw", out var exetendRawForNumbers) ? exetendRawForNumbers : string.Empty,
            quantity.Value,
            price,
            quantity.Value * price);

        logger.LogInformation(
            "[CSV NUMERIC MAP] RowNumber: {RowNumber}, RawRowText: {RawRowText}, Qty raw: {QtyRaw}, Costper raw: {CostperRaw}, Parsed quantity: {ParsedQuantity}, Parsed price: {ParsedPrice}",
            lineNumber,
            rawRowText,
            quantityText,
            costPerText,
            quantity.Value,
            price);

        row = new CsvOrderRowDto
        {
            FileName = fileName,
            RowNumber = lineNumber,
            OrderNumber = orderNumber,
            OrderDate = orderDate,
            DeliveryDate = deliveryDate,
            DistributionCentre = distributionCentre,
            ProductCode = productCode,
            ProductName = productName,
            Product = resolvedProduct,
            Quantity = quantity.Value,
            Price = price,
            Metadata = metadata
        };

        if (!string.IsNullOrWhiteSpace(vendor))
        {
            row.Metadata["Vendor"] = vendor;
        }

        if (!string.IsNullOrWhiteSpace(depot))
        {
            row.Metadata["Depot"] = depot;
        }

        if (!string.IsNullOrWhiteSpace(destId))
        {
            row.Metadata["DestID"] = destId;
        }

        if (!string.IsNullOrWhiteSpace(destDesc))
        {
            row.Metadata["DestDesc"] = destDesc;
        }

        // Trace parsed row values at parse stage.
        logger.LogInformation(
            "[CSV PARSED VALUES] SKU: {Sku}, Raw Qty: {RawQty}, Raw Costper: {RawCostper}, Resolved Quantity: {ResolvedQuantity}, Resolved Price: {ResolvedPrice}, Line Total: {LineTotal}",
            resolvedProduct,
            metadata.TryGetValue("QtyRaw", out var qtyRawForLog) ? qtyRawForLog : string.Empty,
            metadata.TryGetValue("CostperRaw", out var costperRawForLog) ? costperRawForLog : string.Empty,
            quantity.Value,
            price,
            quantity.Value * price);

        return null;
    }

    private static bool TryParseDate(string value, out DateTime result)
    {
        return DateTime.TryParseExact(value, OrderDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out result)
            || DateTime.TryParseExact(value, OrderDateFormats, CultureInfo.CurrentCulture, DateTimeStyles.None, out result)
            || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out result)
            || DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out result);
    }

    private static bool TryParseDecimal(string value, out decimal result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var compact = Regex.Replace(value, @"\s+", string.Empty)
            .Replace("\u00A0", string.Empty)
            .Trim();

        if (decimal.TryParse(compact, NumberStyles.Number, CultureInfo.InvariantCulture, out result))
        {
            return true;
        }

        var normalized = NormalizeDecimalForInvariant(compact);
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out result);
    }

    private static string NormalizeDecimalForInvariant(string value)
    {
        var hasComma = value.Contains(',');
        var hasDot = value.Contains('.');

        if (hasComma && hasDot)
        {
            var lastComma = value.LastIndexOf(',');
            var lastDot = value.LastIndexOf('.');

            if (lastComma > lastDot)
            {
                return value.Replace(".", string.Empty).Replace(',', '.');
            }

            return value.Replace(",", string.Empty);
        }

        if (hasComma)
        {
            return value.Replace(',', '.');
        }

        return value;
    }

    private static decimal? SafeParseDecimal(
        string input,
        List<CsvUploadErrorDto> errors,
        string fileName,
        int rowNumber,
        string fieldName)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var normalized = input.Trim().ToLowerInvariant();
        if (normalized is "n" or "no" || normalized.Length == 0)
        {
            errors.Add(new CsvUploadErrorDto
            {
                FileName = fileName,
                RowNumber = rowNumber,
                Field = fieldName,
                Message = $"Invalid number: '{input}'"
            });

            return null;
        }

        if (normalized is "y" or "yes")
        {
            errors.Add(new CsvUploadErrorDto
            {
                FileName = fileName,
                RowNumber = rowNumber,
                Field = fieldName,
                Message = $"Invalid number: '{input}'"
            });

            return null;
        }

        if (TryParseDecimal(input, out var result))
            return result;

        errors.Add(new CsvUploadErrorDto
        {
            FileName = fileName,
            RowNumber = rowNumber,
            Field = fieldName,
            Message = $"Invalid number: '{input}'"
        });

        return null;
    }

    private static string GetMappedValue(IReadOnlyList<string> values, int? index)
    {
        if (!index.HasValue || index.Value < 0 || index.Value >= values.Count)
            return string.Empty;

        return CleanValue(values[index.Value]);
    }

    private static CsvHeaderMap BuildHeaderMap(IReadOnlyList<string> headers)
    {
        var normalizedHeaders = headers
            .Select((header, index) => new KeyValuePair<string, int>(NormalizeHeader(header), index))
            .ToList();

        var orderNumberIndex = FindHeaderIndex(normalizedHeaders, "ordernumber", "orderno", "order no", "order_no", "orderid", "order");
        var orderDateIndex = FindHeaderIndex(normalizedHeaders, "orderdate", "order date", "order_date", "dateordered");
        var deliveryDateIndex = FindHeaderIndex(normalizedHeaders, "deliverydate", "delivery date", "delivery_date", "required date", "dropdate", "drop date");
        var distributionCentreIndex = FindHeaderIndex(normalizedHeaders, "distributioncentre", "distribution centre", "distributioncenter", "dc", "warehouse", "destination", "destinationdescription");
        var destIdIndex = FindHeaderIndex(normalizedHeaders, "destid", "destinationid", "destination id", "destcode", "destinationcode", "destination code");
        var destDescIndex = FindHeaderIndex(normalizedHeaders, "destdesc", "destinationdescription", "destination description", "destdescription", "destination", "destinationname", "destination name");
        var vendorIndex = FindHeaderIndex(normalizedHeaders, "vendor", "vendorname", "vendor name", "logisticscompany", "logistics company");
        var depotIndex = FindHeaderIndex(normalizedHeaders, "depot", "depotname", "depot name", "logisticsdepot", "logistics depot");
        var productCodeIndex = FindHeaderIndex(normalizedHeaders, "productcode", "product code", "sku", "sku code", "itemnum", "item num", "itemnumber", "item number");
        var productNameIndex = FindHeaderIndex(normalizedHeaders, "productname", "product name", "itemdesc", "item desc", "description");
        var productIndex = FindHeaderIndex(normalizedHeaders, "product", "item");
        var quantityIndex = FindHeaderIndexExact(normalizedHeaders, "qty", "quantity", "qtyordered", "orderedqty", "units");
        var costPerIndex = FindHeaderIndexExact(normalizedHeaders, "costper", "cost per", "cost_per");
        var grossCostIndex = FindHeaderIndexExact(normalizedHeaders, "grosscst", "gross cost", "grosscost");
        var exetendCostIndex = FindHeaderIndexExact(normalizedHeaders, "exetendcst", "extendedcst", "extendcst", "extended cost", "extend cost");

        var missingRequiredColumns = new List<string>();
        if (orderNumberIndex is null)
            missingRequiredColumns.Add("Order Number");
        if (orderDateIndex is null)
            missingRequiredColumns.Add("Order Date");
        if (distributionCentreIndex is null)
            missingRequiredColumns.Add("Distribution Centre");
        if (productCodeIndex is null && productNameIndex is null && productIndex is null)
            missingRequiredColumns.Add("Product");
        if (quantityIndex is null)
            missingRequiredColumns.Add("Quantity");
        if (costPerIndex is null)
            missingRequiredColumns.Add("Costper");

        return new CsvHeaderMap(
            orderNumberIndex,
            orderDateIndex,
            deliveryDateIndex,
            distributionCentreIndex,
            destIdIndex,
            destDescIndex,
            vendorIndex,
            depotIndex,
            productCodeIndex,
            productNameIndex,
            productIndex,
            quantityIndex,
            costPerIndex,
            grossCostIndex,
            exetendCostIndex,
            headers.ToList(),
            missingRequiredColumns);
    }

    private static Dictionary<string, string> BuildMetadata(IReadOnlyList<string> values, CsvHeaderMap headerMap)
    {
        var mappedIndexes = new HashSet<int>(new[]
        {
            headerMap.OrderNumberIndex,
            headerMap.OrderDateIndex,
            headerMap.DeliveryDateIndex,
            headerMap.DistributionCentreIndex,
            headerMap.DestIdIndex,
            headerMap.DestDescIndex,
            headerMap.VendorIndex,
            headerMap.DepotIndex,
            headerMap.ProductCodeIndex,
            headerMap.ProductNameIndex,
            headerMap.ProductIndex,
            headerMap.QuantityIndex,
            headerMap.CostPerIndex,
            headerMap.GrossCostIndex,
            headerMap.ExetendCostIndex
        }.Where(index => index.HasValue).Select(index => index!.Value));

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < values.Count && index < headerMap.Headers.Count; index++)
        {
            if (mappedIndexes.Contains(index))
            {
                continue;
            }

            var header = CleanValue(headerMap.Headers[index]);
            if (string.IsNullOrWhiteSpace(header))
            {
                continue;
            }

            metadata[header] = CleanValue(values[index]);
        }

        return metadata;
    }

    private static int? FindHeaderIndex(IReadOnlyList<KeyValuePair<string, int>> normalizedHeaders, params string[] aliases)
    {
        var exact = FindHeaderIndexExact(normalizedHeaders, aliases);
        if (exact.HasValue)
            return exact;

        var normalizedAliases = aliases.Select(NormalizeHeader).ToArray();

        foreach (var alias in normalizedAliases)
        {
            var partial = normalizedHeaders.FirstOrDefault(pair => pair.Key.Contains(alias, StringComparison.Ordinal));
            if (!string.IsNullOrEmpty(partial.Key))
                return partial.Value;
        }

        return null;
    }

    private static int? FindHeaderIndexExact(IReadOnlyList<KeyValuePair<string, int>> normalizedHeaders, params string[] aliases)
    {
        var normalizedAliases = aliases.Select(NormalizeHeader).ToArray();

        foreach (var alias in normalizedAliases)
        {
            var exact = normalizedHeaders.FirstOrDefault(pair => pair.Key == alias);
            if (!string.IsNullOrEmpty(exact.Key))
                return exact.Value;
        }

        return null;
    }

    private static string NormalizeHeader(string header)
    {
        return Regex.Replace(CleanValue(header).ToLowerInvariant(), @"[^a-z0-9]+", string.Empty);
    }

    private sealed record ParseCsvFileResult(
        List<CsvOrderRowDto> Rows,
        List<CsvUploadErrorDto> Errors,
        int TotalRows,
        int SkippedRows);

    private sealed record CsvHeaderMap(
        int? OrderNumberIndex,
        int? OrderDateIndex,
        int? DeliveryDateIndex,
        int? DistributionCentreIndex,
        int? DestIdIndex,
        int? DestDescIndex,
        int? VendorIndex,
        int? DepotIndex,
        int? ProductCodeIndex,
        int? ProductNameIndex,
        int? ProductIndex,
        int? QuantityIndex,
        int? CostPerIndex,
        int? GrossCostIndex,
        int? ExetendCostIndex,
        List<string> Headers,
        List<string> MissingRequiredColumns);

    private sealed record RowValidationError(string Field, string Message);
}
