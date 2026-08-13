using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;
using System.Globalization;

namespace OrderProcessingApp.Services;

public class OrderService : IOrderService
{
    private readonly AppDbContext _dbContext;
    private readonly IPricingService _pricingService;
    private readonly IPalletService _palletService;
    private readonly IPlanningService _planningService;
    private readonly IDistributionCentreResolver _distributionCentreResolver;
    private readonly IStockService _stockService;
    private readonly IAuditService _auditService;
    private readonly ILogger<OrderService> _logger;

    public OrderService(
        AppDbContext dbContext,
        IPricingService pricingService,
        IPalletService palletService,
        IPlanningService planningService,
        IDistributionCentreResolver distributionCentreResolver,
        IStockService stockService,
        IAuditService auditService,
        ILogger<OrderService> logger)
    {
        _dbContext = dbContext;
        _pricingService = pricingService;
        _palletService = palletService;
        _planningService = planningService;
        _distributionCentreResolver = distributionCentreResolver;
        _stockService = stockService;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<OrderDto> CreateManualOrderAsync(ManualOrderCreateDto dto, CancellationToken cancellationToken = default)
    {
        if (await _dbContext.Orders.AsNoTracking().AnyAsync(x => x.OrderNumber == dto.OrderNumber, cancellationToken))
        {
            throw new InvalidOperationException($"Order number '{dto.OrderNumber}' already exists.");
        }

        var (distributionCentre, distributionCentreWarning) = await ResolveOrCreateDistributionCentreForOrderAsync(
            dto.DistributionCentreId,
            dto.DistributionCentreId.ToString(CultureInfo.InvariantCulture),
            cancellationToken);

        var orderDate = ToDbDate(dto.OrderDate);
        var deliveryDate = ToDbDate(dto.DeliveryDate);

        if (deliveryDate < orderDate)
        {
            throw new InvalidOperationException("Delivery date cannot be earlier than order date.");
        }

        var order = new Order
        {
            OrderNumber = dto.OrderNumber,
            OrderDate = orderDate,
            DeliveryDate = deliveryDate,
            DistributionCentreId = distributionCentre.Id,
            Source = OrderSource.MANUAL,
            Status = OrderStatus.Pending,
            Notes = dto.Notes
        };

        if (!string.IsNullOrWhiteSpace(distributionCentreWarning))
        {
            order.Notes = AppendNote(order.Notes, distributionCentreWarning);
        }

        var orderEvaluation = await BuildOrderItemsAsync(order, dto.Items, distributionCentre, false, cancellationToken);
        order.TotalValue = orderEvaluation.Total;
        order.TotalPallets = orderEvaluation.TotalPallets;
        order.Status = orderEvaluation.HasPricingIssues ? OrderStatus.Flagged : OrderStatus.Validated;
        if (orderEvaluation.Warnings.Count > 0)
        {
            order.Notes = AppendNote(order.Notes, string.Join(" | ", orderEvaluation.Warnings));
        }

        _dbContext.Orders.Add(order);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return await GetOrderByIdAsync(order.Id, cancellationToken)
            ?? throw new InvalidOperationException("Order could not be loaded after creation.");
    }

    public async Task<CsvImportProcessingResult> CreateOrdersFromCsvRowsAsync(List<CsvOrderRowDto> rows, bool allowDuplicates = false, bool createMissingProducts = false, CancellationToken cancellationToken = default)
    {
        var result = new CsvUploadResultDto
        {
            TotalRows = rows.Count
        };
        var pendingRows = new List<CsvOrderRowDto>();
        var productsMissingPricing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var products = await _dbContext.Products
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);

        // Keyed cache: trim+lower SKUCode → Product (includes soft-deleted, prevents duplicate inserts)
        var productsBySku = products
            .Where(p => !string.IsNullOrWhiteSpace(p.SKUCode))
            .GroupBy(p => p.SKUCode.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First());

        var validRows = new List<ValidatedCsvRow>();

        foreach (var row in rows)
        {
            try
            {
                var orderNumber = row.OrderNumber.Trim();
                var distributionCentreInput = row.DistributionCentre.Trim();
                var productCodeInput = CleanProductInput(row.ProductCode ?? string.Empty);
                var productNameInput = CleanProductInput(row.ProductName ?? string.Empty);
                var productInput = GetResolvedProductInput(row);

                _logger.LogInformation(
                    "[CSV ROW START] OrderNumber: {OrderNumber}, SKU: {Sku}, DC: {DistributionCentre}, Quantity: {Quantity}",
                    orderNumber,
                    productCodeInput,
                    distributionCentreInput,
                    row.Quantity);

                if (string.IsNullOrWhiteSpace(orderNumber))
                {
                    _logger.LogWarning("[CSV EARLY EXIT] OrderNumber: {OrderNumber}, Reason: Order number is required.", orderNumber);
                    AddValidationError(result, row, "Order number is required.", "OrderNumber");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(distributionCentreInput))
                {
                    _logger.LogWarning("[CSV EARLY EXIT] OrderNumber: {OrderNumber}, Reason: Distribution centre is required.", orderNumber);
                    AddValidationError(result, row, "Distribution centre is required.", "DistributionCentre");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(productInput))
                {
                    _logger.LogWarning("[CSV EARLY EXIT] OrderNumber: {OrderNumber}, Reason: Product input is empty after SKU mapping.", orderNumber);
                    AddValidationError(result, row, "Product is required.", "Product");
                    continue;
                }

                if (row.Price <= 0)
                {
                    _logger.LogWarning("[CSV EARLY EXIT] OrderNumber: {OrderNumber}, Reason: Price must be greater than 0.", orderNumber);
                    AddValidationError(result, row, "Price must be greater than 0", "Price");
                    continue;
                }

                _logger.LogInformation("[CSV DC RESOLVE START] {DistributionCentre}", distributionCentreInput);
                var dcResolution = await _distributionCentreResolver.ResolveFromCsvAsync(distributionCentreInput, cancellationToken);
                _logger.LogInformation(
                    "[CSV DC RESOLVE RESULT] Found: {Found}, Id: {Id}",
                    dcResolution.IsResolved && dcResolution.DistributionCentre is not null,
                    dcResolution.DistributionCentre?.Id);
                if (!dcResolution.IsResolved || dcResolution.DistributionCentre is null)
                {
                    _logger.LogWarning(
                        "[CSV DC FAILURE] OrderNumber: {OrderNumber}, DistributionCentre: {DistributionCentre}",
                        orderNumber,
                        distributionCentreInput);
                    _logger.LogWarning(
                        "CSV DC resolution unresolved for row. File={FileName}, Row={RowNumber}, OriginalDc='{OriginalDc}', NormalizedDc='{NormalizedDc}'",
                        row.FileName,
                        row.RowNumber,
                        row.DistributionCentre,
                        dcResolution.NormalizedInput);
                    _logger.LogWarning("[CSV EARLY EXIT] OrderNumber: {OrderNumber}, Reason: Distribution centre could not be resolved.", orderNumber);
                    AddMissingDistributionCentre(result, row.DistributionCentre);
                    pendingRows.Add(CloneRow(row));
                    continue;
                }

                var distributionCentre = dcResolution.DistributionCentre;
                if (!distributionCentre.IsActive)
                {
                    var restorableDistributionCentre = await _dbContext.DistributionCentres
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(x => x.Id == distributionCentre.Id, cancellationToken);

                    if (restorableDistributionCentre is null)
                    {
                        throw new InvalidOperationException($"Distribution centre '{distributionCentreInput}' was resolved but could not be loaded for restoration.");
                    }

                    restorableDistributionCentre.IsActive = true;
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    distributionCentre = restorableDistributionCentre;

                    _logger.LogInformation(
                        "[CSV DC RESTORED] DistributionCentreId: {DistributionCentreId}, DistributionCentreName: {DistributionCentreName}",
                        distributionCentre.Id,
                        distributionCentre.Name);
                }

                var productSkuRaw = !string.IsNullOrWhiteSpace(row.ProductCode)
                    ? row.ProductCode
                    : !string.IsNullOrWhiteSpace(row.Product)
                        ? row.Product
                        : !string.IsNullOrWhiteSpace(row.ProductName)
                            ? row.ProductName
                            : (row.Metadata.TryGetValue("SKU", out var skuFromMetadata) ? skuFromMetadata : null);
                var productNameRaw = row.ProductName;
                var productSku = CleanProductInput(productSkuRaw ?? string.Empty);
                _logger.LogInformation(
                    "[CSV SKU MAP] RawCode: '{RawCode}', Product: '{Product}', ProductName: '{ProductName}', ChosenSku: '{Sku}'",
                    row.ProductCode,
                    row.Product,
                    row.ProductName,
                    productSku);

                if (string.IsNullOrWhiteSpace(productSkuRaw))
                {
                    _logger.LogWarning("[CSV EARLY EXIT] OrderNumber: {OrderNumber}, Reason: SKU is empty after mapping.", orderNumber);
                    throw new Exception("[CSV CRITICAL] Product SKU not mapped correctly from CSV");
                }
                var productName = string.IsNullOrWhiteSpace(productNameRaw)
                    ? productSku
                    : CleanProductInput(productNameRaw);

                _logger.LogInformation("[CSV PRODUCT RESOLVE ENTER] SKU: {Sku}", productSku);
                var (product, productCreated) = await ResolveOrCreateProductAsync(productSku, productName, productsBySku, cancellationToken);

                _logger.LogInformation(
                    "[CSV ROW RESULT] OrderNumber: {OrderNumber}, ProductCreated: {ProductCreated}, ProductId: {ProductId}",
                    orderNumber,
                    productCreated,
                    product.Id);

                validRows.Add(new ValidatedCsvRow(
                    row,
                    orderNumber,
                    ToDbDate(row.OrderDate),
                    ToDbDate(row.DeliveryDate),
                    distributionCentre,
                    product));
            }
            catch (Exception exception)
            {
                var sku = row.ProductCode ?? row.Product ?? row.ProductName;
                var orderNumber = row.OrderNumber?.Trim() ?? string.Empty;
                _logger.LogWarning("[CSV EARLY EXIT] OrderNumber: {OrderNumber}, Reason: {Reason}", orderNumber, exception.Message);
                _logger.LogInformation(
                    "[CSV ROW RESULT] OrderNumber: {OrderNumber}, ProductCreated: {ProductCreated}, ProductId: {ProductId}",
                    orderNumber,
                    false,
                    (int?)null);
                _logger.LogError("[CSV ROW FAILURE] SKU: {Sku}, ERROR: {Error}", sku, exception.Message);
                throw;
            }
        }

        var groupedRows = validRows.GroupBy(x => new
        {
            x.OrderNumber,
            x.OrderDate,
            x.DeliveryDate,
            x.DistributionCentre.Id
        });

        foreach (var group in groupedRows)
        {
            var orderNumber = group.Key.OrderNumber;

            try
            {
                var alreadyExists = await _dbContext.Orders
                    .AsNoTracking()
                    .AnyAsync(x => x.OrderNumber == orderNumber, cancellationToken);

                if (alreadyExists)
                {
                    if (!allowDuplicates)
                    {
                        result.SkippedOrders++;
                        foreach (var row in group)
                        {
                            AddValidationError(result, row.Row, $"Duplicate order '{orderNumber}' skipped because duplicate orders are not allowed.", "OrderNumber");
                        }

                        continue;
                    }

                    var baseNumber = orderNumber;
                    var existingCount = await _dbContext.Orders
                        .AsNoTracking()
                        .CountAsync(x => x.OrderNumber == baseNumber || x.OrderNumber.StartsWith(baseNumber + "-"), cancellationToken);
                    orderNumber = $"{baseNumber}-{existingCount}";
                }

                var orderDate = group.Key.OrderDate;
                var deliveryDate = group.Key.DeliveryDate;
                if (deliveryDate < orderDate)
                {
                    result.SkippedOrders++;
                    foreach (var row in group)
                    {
                        AddValidationError(result, row.Row, $"Delivery date cannot be earlier than order date for order '{orderNumber}'.", "DeliveryDate");
                    }

                    continue;
                }

                var order = new Order
                {
                    OrderNumber = orderNumber,
                    OrderDate = orderDate,
                    DeliveryDate = deliveryDate,
                    DistributionCentreId = group.First().DistributionCentre.Id,
                    Source = OrderSource.CSV,
                    Status = OrderStatus.Pending
                };

                var itemDtos = group.Select(validRow => new OrderItemCreateDto
                {
                    ProductId = validRow.Product.Id,
                    ProductCode = ResolveOrderItemProductCode(validRow.Product, validRow.Row),
                    ProductName = ResolveOrderItemProductName(validRow.Product, validRow.Row),
                    Quantity = ResolveCsvQuantity(validRow.Row),
                    Price = ResolveCsvUnitPrice(validRow.Row),
                    IsUnmapped = !validRow.Product.IsMapped,
                    Metadata = BuildCsvItemMetadata(validRow.Row)
                }).ToList();

                foreach (var item in itemDtos)
                {
                    _logger.LogInformation(
                        "[BACKEND ITEM MAP] SKU: {Sku}, Raw Qty: {RawQty}, Raw Costper: {RawCostper}, Resolved Quantity: {ResolvedQuantity}, Resolved Price: {ResolvedPrice}, Line Total: {LineTotal}",
                        item.ProductCode ?? string.Empty,
                        item.Metadata.TryGetValue("CsvRawQty", out var rawQty) ? rawQty : string.Empty,
                        item.Metadata.TryGetValue("CsvRawCostper", out var rawCostper) ? rawCostper : string.Empty,
                        item.Quantity,
                        item.Price,
                        item.Quantity * item.Price);

                    if (IsQtyCostperSwap(item.Metadata, item.Quantity, item.Price ?? 0m))
                    {
                        _logger.LogError(
                            "[BACKEND SWAP DETECTED] Stage: {Stage}, SKU: {Sku}, RawQty: {RawQty}, RawCostper: {RawCostper}, Quantity: {Quantity}, UnitPrice: {UnitPrice}",
                            "OrderItemCreateDto",
                            item.ProductCode ?? string.Empty,
                            item.Metadata.TryGetValue("CsvRawQty", out var mapRawQty) ? mapRawQty : string.Empty,
                            item.Metadata.TryGetValue("CsvRawCostper", out var mapRawCostper) ? mapRawCostper : string.Empty,
                            item.Quantity,
                            item.Price ?? 0m);
                    }
                }

                if (itemDtos.Count == 0)
                {
                    result.SkippedOrders++;
                    foreach (var row in group)
                    {
                        AddValidationError(result, row.Row, $"No valid order items found for order '{orderNumber}'.", "Product");
                    }

                    continue;
                }

                var orderEvaluation = await BuildOrderItemsAsync(order, itemDtos, group.First().DistributionCentre, true, cancellationToken);
                order.TotalValue = orderEvaluation.Total;
                order.TotalPallets = orderEvaluation.TotalPallets;
                order.Status = orderEvaluation.HasPricingIssues ? OrderStatus.Flagged : OrderStatus.Validated;

                foreach (var item in order.Items.Where(item => item.IsPriceMissing))
                {
                    var productCode = NormalizeProductCode(item.ProductCode ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(productCode))
                    {
                        productCode = NormalizeProductCode(item.Product?.SKUCode ?? string.Empty);
                    }

                    if (!string.IsNullOrWhiteSpace(productCode))
                    {
                        productsMissingPricing.Add(productCode);
                    }
                }

                _dbContext.Orders.Add(order);
                _logger.LogInformation("[CSV] Creating order: {OrderNumber}", orderNumber);
                await SaveCsvImportChangesAsync(cancellationToken, orderNumber);
                result.CreatedOrders++;
                if (order.Status == OrderStatus.Flagged)
                {
                    result.FlaggedOrders++;
                }
            }
            catch (Exception exception)
            {
                var sku = group.FirstOrDefault()?.Row.ProductCode
                    ?? group.FirstOrDefault()?.Row.Product
                    ?? group.FirstOrDefault()?.Row.ProductName
                    ?? string.Empty;
                _logger.LogError("[CSV ROW FAILURE] SKU: {Sku}, ERROR: {Error}", sku, exception.Message);
                throw;
            }
        }

        if (productsMissingPricing.Count > 0)
        {
            result.Errors.Add($"{productsMissingPricing.Count} products are using CSV pricing (no system price configured)");
        }

        result.RequiresUserAction = result.MissingDistributionCentres.Count > 0 || pendingRows.Count > 0;
        result.Success = !result.RequiresUserAction;
        if (result.MissingProducts.Count > 0 && result.CreatedOrders > 0)
        {
            result.Message = "Orders imported with placeholder products.";
        }

        _logger.LogInformation(
            "CSV import completed. Total rows: {TotalRows}, created orders: {CreatedOrders}, skipped orders: {SkippedOrders}, flagged orders: {FlaggedOrders}, validation errors: {ValidationErrorCount}",
            result.TotalRows,
            result.CreatedOrders,
            result.SkippedOrders,
            result.FlaggedOrders,
            result.ValidationErrors.Count);

        var totalProducts = await _dbContext.Products.CountAsync(cancellationToken);
        _logger.LogError("[CSV FINAL CHECK] Total products in DB: {Count}", totalProducts);

        return new CsvImportProcessingResult
        {
            Result = result,
            PendingRows = pendingRows
        };
    }

    public async Task<CreateMissingDistributionCentresResultDto> CreateMissingDistributionCentresAsync(CreateMissingDistributionCentresRequestDto dto, CancellationToken cancellationToken = default)
    {
        var requestedCentres = dto.Centres
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(CleanDistributionCentreName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requestedCentres.Count == 0)
        {
            throw new InvalidOperationException("At least one distribution centre name is required.");
        }

        var regionId = await _dbContext.Regions
            .AsNoTracking()
            .OrderBy(region => region.Id)
            .Select(region => (int?)region.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (!regionId.HasValue)
        {
            throw new InvalidOperationException("At least one region is required before creating distribution centres.");
        }

        var existingCentres = await _dbContext.DistributionCentres
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);

        var result = new CreateMissingDistributionCentresResultDto
        {
            DistributionCentreId = dto.DistributionCentreId ?? 0
        };
        var hasChanges = false;

        foreach (var centreName in requestedCentres)
        {
            var normalizedCentreName = Normalize(centreName);
            if (string.IsNullOrWhiteSpace(normalizedCentreName))
            {
                continue;
            }

            var matchingCentre = existingCentres
                .Where(dc => Normalize(dc.Name) == normalizedCentreName || Normalize(dc.Code) == normalizedCentreName)
                .OrderByDescending(dc => dc.IsActive)
                .FirstOrDefault();

            if (matchingCentre is not null)
            {
                if (!matchingCentre.IsActive)
                {
                    matchingCentre.IsActive = true;
                    hasChanges = true;
                    _logger.LogInformation(
                        "[CSV DC RESTORE] Restored inactive distribution centre. Id: {DistributionCentreId}, Name: {DistributionCentreName}, Code: {DistributionCentreCode}",
                        matchingCentre.Id,
                        matchingCentre.Name,
                        matchingCentre.Code);
                }

                result.ExistingCentres.Add(centreName);
                continue;
            }

            Console.WriteLine($"Creating distribution centre: {centreName}");

            var entity = new DistributionCentre
            {
                Name = centreName,
                Code = centreName,
                RegionId = regionId.Value,
                RequiresAttention = true
            };

            _dbContext.DistributionCentres.Add(entity);
            existingCentres.Add(entity);
            result.CreatedCentres.Add(centreName);
            hasChanges = true;
        }

        if (hasChanges)
        {
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception)
            {
                var innerError = exception.InnerException?.Message ?? exception.Message;
                throw new InvalidOperationException($"Could not create or restore distribution centres due to a uniqueness conflict. Details: {innerError}");
            }
        }

        return result;
    }

    private static void AddValidationError(CsvUploadResultDto result, CsvOrderRowDto row, string message, string? field = null)
    {
        result.ValidationErrors.Add(new CsvUploadErrorDto
        {
            FileName = row.FileName,
            RowNumber = row.RowNumber,
            Field = field,
            Message = message
        });
    }

    private static void AddMissingDistributionCentre(CsvUploadResultDto result, string distributionCentre)
    {
        var cleanedName = CleanDistributionCentreName(distributionCentre);
        if (string.IsNullOrWhiteSpace(cleanedName))
            return;

        if (!result.MissingDistributionCentres.Contains(cleanedName, StringComparer.OrdinalIgnoreCase))
        {
            result.MissingDistributionCentres.Add(cleanedName);
        }
    }

    private static void AddMissingProduct(CsvUploadResultDto result, string product)
    {
        var cleanedProduct = CleanProductInput(product);
        if (string.IsNullOrWhiteSpace(cleanedProduct))
            return;

        if (!result.MissingProducts.Contains(cleanedProduct, StringComparer.OrdinalIgnoreCase))
        {
            result.MissingProducts.Add(cleanedProduct);
        }
    }

    private static void AddMissingProduct(CsvUploadResultDto result, CsvOrderRowDto row)
    {
        AddMissingProduct(result, GetResolvedProductInput(row));
    }

    private static Product? ResolveProduct(IEnumerable<Product> products, string input)
    {
        var normalizedInput = Normalize(input);
        if (string.IsNullOrWhiteSpace(normalizedInput))
            return null;

        var codeMatch = products.FirstOrDefault(product => Normalize(product.SKUCode) == normalizedInput);
        if (codeMatch is not null)
            return codeMatch;

        return products.FirstOrDefault(product => Normalize(product.Name) == normalizedInput);
    }

    private static Product? ResolveProduct(IEnumerable<Product> products, string productCode, string productName, string fallbackProduct)
    {
        if (!string.IsNullOrWhiteSpace(productCode))
        {
            var codeMatch = ResolveProduct(products, productCode);
            if (codeMatch is not null)
                return codeMatch;
        }

        if (!string.IsNullOrWhiteSpace(productName))
        {
            var nameMatch = products.FirstOrDefault(product => Normalize(product.Name) == Normalize(productName));
            if (nameMatch is not null)
                return nameMatch;
        }

        return ResolveProduct(products, fallbackProduct);
    }

    private static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var normalized = input.Trim();
        normalized = normalized.Trim('"', '\'', '“', '”');
        normalized = normalized.ToLowerInvariant();
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"[\uFEFF\u200B\u200C\u200D]", string.Empty);
        normalized = normalized.Replace("\t", " ");
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\s+", " ");

        return normalized.Trim();
    }

    private static string CleanDistributionCentreName(string input)
    {
        return input.Trim().Trim('"', '\'', '“', '”').Trim();
    }

    private async Task<(DistributionCentre DistributionCentre, string? Warning)> ResolveOrCreateDistributionCentreForOrderAsync(int distributionCentreId, string fallbackInput, CancellationToken cancellationToken)
    {
        var distributionCentre = await _dbContext.DistributionCentres
            .FirstOrDefaultAsync(x => x.Id == distributionCentreId, cancellationToken);

        if (distributionCentre is not null)
        {
            return (distributionCentre, null);
        }

        throw new InvalidOperationException($"Invalid distribution centre: {distributionCentreId}.");
    }

    private static string CleanProductInput(string input)
    {
        return input.Trim().Trim('"', '\'', '“', '”').Trim();
    }

    private static string NormalizeProductCode(string input)
    {
        var normalized = Normalize(input);
        return normalized.Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    private static string GetResolvedProductInput(CsvOrderRowDto row)
    {
        var productCode = CleanProductInput(row.ProductCode);
        if (!string.IsNullOrWhiteSpace(productCode))
            return productCode;

        var productName = CleanProductInput(row.ProductName);
        if (!string.IsNullOrWhiteSpace(productName))
            return productName;

        return CleanProductInput(row.Product);
    }

    private static CsvOrderRowDto CloneRow(CsvOrderRowDto row)
    {
        return new CsvOrderRowDto
        {
            FileName = row.FileName,
            RowNumber = row.RowNumber,
            OrderNumber = row.OrderNumber,
            OrderDate = row.OrderDate,
            DeliveryDate = row.DeliveryDate,
            DistributionCentre = row.DistributionCentre,
            ProductCode = row.ProductCode,
            ProductName = row.ProductName,
            Product = row.Product,
            Quantity = row.Quantity,
            Price = row.Price,
            Metadata = new Dictionary<string, string>(row.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static Dictionary<string, string> BuildCsvItemMetadata(CsvOrderRowDto row)
    {
        var metadata = new Dictionary<string, string>(row.Metadata, StringComparer.OrdinalIgnoreCase);
        metadata["OriginalDistributionCentre"] = row.DistributionCentre;
        metadata["CsvRawQty"] = row.Metadata.TryGetValue("QtyRaw", out var qtyRaw)
            ? qtyRaw
            : row.Quantity.ToString(CultureInfo.InvariantCulture);
        metadata["CsvRawCostper"] = row.Metadata.TryGetValue("CostperRaw", out var costperRaw)
            ? costperRaw
            : row.Price.ToString(CultureInfo.InvariantCulture);
        metadata["GrossCstRaw"] = row.Metadata.TryGetValue("GrossCstRaw", out var grossCstRaw)
            ? grossCstRaw
            : string.Empty;
        metadata["ExetendCstRaw"] = row.Metadata.TryGetValue("ExetendCstRaw", out var exetendCstRaw)
            ? exetendCstRaw
            : string.Empty;
        return metadata;
    }

    private static decimal ResolveCsvQuantity(CsvOrderRowDto row)
    {
        if (row.Metadata.TryGetValue("QtyRaw", out var rawQty)
            && TryParseInvariantDecimal(rawQty, out var parsedQty))
        {
            return parsedQty;
        }

        return row.Quantity;
    }

    private static decimal ResolveCsvUnitPrice(CsvOrderRowDto row)
    {
        if (row.Metadata.TryGetValue("CostperRaw", out var rawCostper)
            && TryParseInvariantDecimal(rawCostper, out var parsedCostper))
        {
            return parsedCostper;
        }

        return row.Price;
    }

    private static bool IsQtyCostperSwap(Dictionary<string, string> metadata, decimal quantity, decimal unitPrice)
    {
        if (!TryGetRawValue(metadata, "QtyRaw", "CsvRawQty", out _, out var parsedQty)
            || !TryGetRawValue(metadata, "CostperRaw", "CsvRawCostper", out _, out var parsedCostper))
        {
            return false;
        }

        return !NearlyEqual(parsedQty, parsedCostper)
            && NearlyEqual(quantity, parsedCostper)
            && NearlyEqual(unitPrice, parsedQty);
    }

    private static bool TryParseInvariantDecimal(string value, out decimal parsed)
    {
        parsed = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var compact = value.Trim().Replace(" ", string.Empty).Replace("\u00A0", string.Empty);
        if (decimal.TryParse(compact, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed))
        {
            return true;
        }

        compact = compact.Replace(',', '.');
        return decimal.TryParse(compact, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed);
    }

    private static bool NearlyEqual(decimal left, decimal right)
    {
        return Math.Abs(left - right) <= 0.01m;
    }

    private static bool TryGetRawValue(
        Dictionary<string, string> metadata,
        string primaryKey,
        string fallbackKey,
        out string rawText,
        out decimal rawValue)
    {
        rawValue = 0m;
        rawText = string.Empty;

        if (metadata.TryGetValue(primaryKey, out var primary) && !string.IsNullOrWhiteSpace(primary))
        {
            rawText = primary;
        }
        else if (metadata.TryGetValue(fallbackKey, out var fallback) && !string.IsNullOrWhiteSpace(fallback))
        {
            rawText = fallback;
        }

        return !string.IsNullOrWhiteSpace(rawText)
            && TryParseInvariantDecimal(rawText, out rawValue);
    }

    private async Task<(Product Product, bool Created)> ResolveOrCreateProductAsync(string sku, string name, Dictionary<string, Product> productsBySku, CancellationToken cancellationToken = default)
    {
        var skuKey = sku?.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(skuKey))
            throw new Exception("[CSV CRITICAL] SKU is empty");

        var skuValue = sku?.Trim() ?? throw new Exception("[CSV CRITICAL] SKU is empty");

        var existsInCache = productsBySku.ContainsKey(skuKey);
        _logger.LogInformation("[CSV CACHE CHECK] SKU: {SkuKey}, ExistsInCache: {ExistsInCache}", skuKey, existsInCache);

        if (existsInCache)
        {
            var cached = productsBySku[skuKey];
            if (!cached.IsActive)
            {
                cached.IsActive = true;
                await _dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("[CSV PRODUCT REACTIVATED] SKU: {Sku}, ProductId: {ProductId}", sku, cached.Id);
                _logger.LogInformation("[CSV PRODUCT ACTIVE CHECK] SKU: {Sku}, IsActive: {IsActive}", sku, cached.IsActive);
            }

            productsBySku[skuKey] = cached;
            return (cached, false);
        }

        _logger.LogInformation("[CSV DB LOOKUP] SKU: {SkuKey}", skuKey);

        var existing = await _dbContext.Products
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                p => p.SKUCode != null && p.SKUCode.Trim().ToLower() == skuKey,
                cancellationToken);

        _logger.LogInformation("[CSV DB RESULT] Found: {Found}, ProductId: {ProductId}", existing is not null, existing?.Id);

        if (existing is not null)
        {
            if (!existing.IsActive)
            {
                existing.IsActive = true;
                await _dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("[CSV PRODUCT REACTIVATED] SKU: {Sku}, ProductId: {ProductId}", sku, existing.Id);
            }

            _logger.LogInformation("[CSV PRODUCT ACTIVE CHECK] SKU: {Sku}, IsActive: {IsActive}", sku, existing.IsActive);
            productsBySku[skuKey] = existing;
            return (existing, false);
        }

        _logger.LogInformation("[CSV CREATE START] SKU: {Sku}", sku);

        var newProduct = new Product
        {
            SKUCode = skuValue,
            Name = string.IsNullOrWhiteSpace(name) ? skuValue : name.Trim(),
            PalletConversionRate = 1m,
            IsMapped = false,
            RequiresAttention = true,
            IsActive = true,
            CreatedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified)
        };

        _dbContext.Products.Add(newProduct);

        var rows = await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("[CSV CREATE SAVE RESULT] SKU: {Sku}, Rows: {Rows}", sku, rows);
        if (rows <= 0)
            throw new Exception($"[CSV CRITICAL] Save failed for SKU: {sku}");

        var verify = await _dbContext.Products
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                p => p.SKUCode != null && p.SKUCode.Trim().ToLower() == skuKey,
                cancellationToken);

        if (verify is null)
            throw new Exception($"[CSV CRITICAL] Product NOT persisted: {sku}");

        _logger.LogInformation("[CSV CREATE VERIFIED] SKU: {Sku}, ProductId: {ProductId}", sku, verify.Id);

        productsBySku[skuKey] = verify;

        if (verify is null)
            throw new Exception("[CSV CRITICAL] NULL PRODUCT RETURN");

        return (verify, true);
    }

    private async Task SaveCsvImportChangesAsync(CancellationToken cancellationToken, string context)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            var innerError = exception.InnerException?.Message ?? exception.Message;
            var baseException = exception.GetBaseException();
            var postgresException = baseException as PostgresException;

            if (postgresException is not null)
            {
                Console.Error.WriteLine(
                    "[CSV_POSTGRES_DIAGNOSTIC] ExceptionType={0} InnerExceptionType={1} BaseExceptionType={2} SqlState={3} MessageText={4} Detail={5} Hint={6} ConstraintName={7} TableName={8} ColumnName={9} SchemaName={10} Where={11}",
                    exception.GetType().FullName,
                    exception.InnerException?.GetType().FullName,
                    baseException.GetType().FullName,
                    postgresException.SqlState,
                    postgresException.MessageText,
                    postgresException.Detail,
                    postgresException.Hint,
                    postgresException.ConstraintName,
                    postgresException.TableName,
                    postgresException.ColumnName,
                    postgresException.SchemaName,
                    postgresException.Where);
            }
            else
            {
                Console.Error.WriteLine(
                    "[CSV_POSTGRES_DIAGNOSTIC] BaseExceptionType={0} BaseExceptionMessage={1}",
                    baseException.GetType().FullName,
                    baseException.Message);
            }

            var postgresException2 = exception.InnerException as PostgresException;

            if (postgresException2 is not null)
            {
                _logger.LogError(
                    exception,
                    "[CSV IMPORT ERROR] PostgresException details. Context: {Context}. ExceptionType: {ExceptionType}. InnerExceptionType: {InnerExceptionType}. SqlState: {SqlState}. MessageText: {MessageText}. Detail: {Detail}. Hint: {Hint}. ConstraintName: {ConstraintName}. TableName: {TableName}. ColumnName: {ColumnName}. SchemaName: {SchemaName}. Where: {Where}",
                    context,
                    exception.GetType().FullName,
                    exception.InnerException?.GetType().FullName,
                    postgresException2.SqlState,
                    postgresException2.MessageText,
                    postgresException2.Detail,
                    postgresException2.Hint,
                    postgresException2.ConstraintName,
                    postgresException2.TableName,
                    postgresException2.ColumnName,
                    postgresException2.SchemaName,
                    postgresException2.Where);
            }
            else
            {
                _logger.LogError(
                    exception,
                    "[CSV IMPORT ERROR] Context: {Context}. ExceptionType: {ExceptionType}. InnerExceptionType: {InnerExceptionType}. Details: {InnerError}",
                    context,
                    exception.GetType().FullName,
                    exception.InnerException?.GetType().FullName,
                    innerError);
            }

            _logger.LogError(
                exception,
                "[CSV IMPORT ERROR] ProductId missing or FK violation. Context: {Context}. Details: {InnerError}",
                context,
                innerError);
            throw;
        }
    }

    private static string ResolveOrderItemProductCode(Product product, CsvOrderRowDto? row = null)
    {
        if (!product.IsMapped && row is not null && !string.IsNullOrWhiteSpace(NormalizeProductCode(row.ProductCode)))
        {
            return NormalizeProductCode(row.ProductCode);
        }

        return NormalizeProductCode(product.SKUCode);
    }

    private static string ResolveOrderItemProductName(Product product, CsvOrderRowDto? row = null)
    {
        if (!product.IsMapped && row is not null)
        {
            return CleanProductInput(row.ProductName);
        }

        return CleanProductInput(product.Name);
    }

    private static Product? ResolveProductBySku(IEnumerable<Product> products, string productCode)
    {
        var normalizedProductCode = NormalizeProductCode(productCode);
        if (string.IsNullOrWhiteSpace(normalizedProductCode))
        {
            return null;
        }

        return products.FirstOrDefault(product => NormalizeProductCode(product.SKUCode) == normalizedProductCode);
    }

    private async Task<int> ResolveSourceDistributionCentreIdAsync(int? distributionCentreId, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Resolving source DistributionCentreId: {distributionCentreId?.ToString() ?? "null"}");

        if (!distributionCentreId.HasValue || distributionCentreId.Value <= 0)
        {
            throw new InvalidOperationException("DistributionCentreId is required.");
        }

        var exists = await _dbContext.DistributionCentres
            .AsNoTracking()
            .AnyAsync(dc => dc.Id == distributionCentreId.Value, cancellationToken);

        if (!exists)
        {
            throw new InvalidOperationException("Invalid distribution centre.");
        }

        return distributionCentreId.Value;
    }

    private static string BuildMissingPriceWarning(string productName, string distributionCentreName)
        => $"Price not configured in system for product '{productName}' in distribution centre '{distributionCentreName}' (using CSV price)";

    private static string BuildGroupedMissingPriceWarning(int productCount)
        => $"{productCount} products missing system pricing (using CSV price)";

    private static string BuildDistributionCentreWarning(string input, string resolvedName)
        => $"Distribution centre '{input}' normalized to '{resolvedName}'.";

    private static string BuildDistributionCentreCreatedWarning(string input)
        => $"Distribution centre '{input}' was auto-created for this order and requires attention.";

    private sealed record ValidatedCsvRow(
        CsvOrderRowDto Row,
        string OrderNumber,
        DateTime OrderDate,
        DateTime DeliveryDate,
        DistributionCentre DistributionCentre,
        Product Product);

    public async Task<List<OrderDto>> GetOrdersAsync(CancellationToken cancellationToken = default)
    {
        var orders = await _dbContext.Orders
            .AsNoTracking()
            .Include(x => x.DistributionCentre)
            .Include(x => x.Items)
                .ThenInclude(x => x.Product)
            .OrderByDescending(x => x.OrderDate)
            .ToListAsync(cancellationToken);

        var pricingCache = await GetPricingCacheAsync(orders, cancellationToken);
        var orderDtos = new List<OrderDto>(orders.Count);
        foreach (var order in orders)
        {
            orderDtos.Add(await MapOrderToDtoWithLivePricingAsync(order, pricingCache, cancellationToken));
        }

        return orderDtos;
    }

    public async Task<List<OrderDto>> GetFilteredOrdersAsync(OrderFilterDto filter, CancellationToken cancellationToken = default)
    {
        var orderNumber = string.IsNullOrEmpty(filter.OrderNumber) ? null : filter.OrderNumber.Trim();
        var productCode = string.IsNullOrEmpty(filter.ProductCode) ? null : filter.ProductCode.Trim();
        var productName = string.IsNullOrEmpty(filter.ProductName) ? null : filter.ProductName.Trim();
        var query = _dbContext.Orders
            .AsNoTracking()
            .Include(x => x.DistributionCentre)
            .Include(x => x.Items)
                .ThenInclude(x => x.Product)
            .AsQueryable();

        if (filter.Status.HasValue)
        {
            query = query.Where(x => (int)x.Status == filter.Status.Value);
        }

        if (filter.DistributionCentreId.HasValue)
        {
            query = query.Where(x => x.DistributionCentreId == filter.DistributionCentreId.Value);
        }

        if (filter.StartDate.HasValue)
        {
            var start = ToDbDate(filter.StartDate.Value.Date);
            query = query.Where(x => x.OrderDate >= start);
        }

        if (filter.EndDate.HasValue)
        {
            var end = ToDbDate(filter.EndDate.Value.Date);
            query = query.Where(x => x.OrderDate <= end);
        }

        if (!string.IsNullOrEmpty(orderNumber))
        {
            query = query.Where(x => x.OrderNumber.Contains(orderNumber));
        }

        if (!string.IsNullOrEmpty(productCode))
        {
            query = query.Where(x => x.Items.Any(item =>
                (item.ProductCode != null && item.ProductCode.Contains(productCode)) ||
                (item.Product != null && item.Product.SKUCode.Contains(productCode))));
        }

        if (!string.IsNullOrEmpty(productName))
        {
            query = query.Where(x => x.Items.Any(item =>
                (item.ProductName != null && item.ProductName.Contains(productName)) ||
                (item.Product != null && item.Product.Name.Contains(productName))));
        }

        var orders = await query
            .OrderByDescending(x => x.OrderDate)
            .ToListAsync(cancellationToken);

        var pricingCache = await GetPricingCacheAsync(orders, cancellationToken);
        var orderDtos = new List<OrderDto>(orders.Count);
        foreach (var order in orders)
        {
            orderDtos.Add(await MapOrderToDtoWithLivePricingAsync(order, pricingCache, cancellationToken));
        }

        return orderDtos;
    }

    public async Task<OrderDto?> GetOrderByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.Orders
            .AsNoTracking()
            .Include(x => x.DistributionCentre)
            .Include(x => x.Items)
                .ThenInclude(x => x.Product)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (order is null)
        {
            return null;
        }

        var pricingCache = await GetPricingCacheAsync(new[] { order }, cancellationToken);
        return await MapOrderToDtoWithLivePricingAsync(order, pricingCache, cancellationToken);
    }

    private async Task<Dictionary<(int ProductId, int DistributionCentreId), EffectivePriceResult>> GetPricingCacheAsync(IEnumerable<Order> orders, CancellationToken cancellationToken)
    {
        var keys = orders
            .SelectMany(order => order.Items.Select(item => (item.ProductId, order.DistributionCentreId)))
            .Distinct()
            .ToList();

        if (keys.Count == 0)
        {
            return new Dictionary<(int ProductId, int DistributionCentreId), EffectivePriceResult>();
        }

        return await _pricingService.GetEffectivePricesAsync(keys, null, cancellationToken);
    }

    private async Task<OrderDto> MapOrderToDtoWithLivePricingAsync(Order order, Dictionary<(int ProductId, int DistributionCentreId), EffectivePriceResult> pricingCache, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Order {order.Id} status: {order.Status}");

        var itemDtos = new List<OrderItemDto>(order.Items.Count);

        foreach (var item in order.Items)
        {
            var key = (item.ProductId, order.DistributionCentreId);
            var livePrice = pricingCache.TryGetValue(key, out var cachedPrice)
                ? cachedPrice
                : await _pricingService.GetEffectivePriceAsync(item.ProductId, order.DistributionCentreId, null, cancellationToken);
            var normalizedSystemPrice = livePrice.EffectivePrice.HasValue
                ? Math.Round(livePrice.EffectivePrice.Value, 2)
                : (decimal?)null;
            var normalizedOrderPrice = Math.Round(item.Price, 2);
            var isPriceMissing = !livePrice.IsFound || !normalizedSystemPrice.HasValue;
            var isPriceMismatch = !isPriceMissing && normalizedSystemPrice.Value != normalizedOrderPrice;

            var dto = new OrderItemDto
            {
                Id = item.Id,
                ProductId = item.ProductId,
                ProductName = item.ProductName ?? item.Product?.Name ?? string.Empty,
                ProductCode = item.ProductCode ?? item.Product?.SKUCode ?? string.Empty,
                SKUCode = item.ProductCode ?? item.Product?.SKUCode ?? string.Empty,
                Quantity = item.Quantity,
                Price = item.Price,
                Pallets = item.Pallets,
                LineTotal = item.Quantity * item.Price,
                IsUnmapped = item.IsUnmapped || item.Product?.IsMapped == false,
                BasePrice = livePrice.BasePrice,
                PromoPrice = livePrice.PromoPrice,
                EffectivePrice = livePrice.EffectivePrice,
                IsPriceMissing = isPriceMissing,
                IsPriceMismatch = isPriceMismatch,
                IsCsvPrice = item.IsCsvPrice
            };

            _logger.LogInformation(
                "[BACKEND ITEM DTO] SKU: {Sku}, RawQty: {RawQty}, RawCostper: {RawCostper}, ResolvedQuantity: {ResolvedQuantity}, ResolvedPrice: {ResolvedPrice}, LineTotal: {LineTotal}",
                dto.SKUCode,
                item.Metadata.TryGetValue("CsvRawQty", out var rawQty) ? rawQty : string.Empty,
                item.Metadata.TryGetValue("CsvRawCostper", out var rawCostper) ? rawCostper : string.Empty,
                dto.Quantity,
                dto.Price,
                dto.LineTotal);

            if (IsQtyCostperSwap(item.Metadata, dto.Quantity, dto.Price))
            {
                _logger.LogError(
                    "[BACKEND SWAP DETECTED] Stage: {Stage}, SKU: {Sku}, RawQty: {RawQty}, RawCostper: {RawCostper}, Quantity: {Quantity}, UnitPrice: {UnitPrice}",
                    "OrderItemDto",
                    dto.SKUCode,
                    item.Metadata.TryGetValue("CsvRawQty", out var dtoRawQty) ? dtoRawQty : string.Empty,
                    item.Metadata.TryGetValue("CsvRawCostper", out var dtoRawCostper) ? dtoRawCostper : string.Empty,
                    dto.Quantity,
                    dto.Price);
            }

            itemDtos.Add(dto);
        }

        var hasMissing = itemDtos.Any(i => i.IsPriceMissing);
        var hasMismatch = itemDtos.Any(i => i.IsPriceMismatch);
        Console.WriteLine($"DTO -> Order {order.Id}: Missing={hasMissing}, Mismatch={hasMismatch}");

        return new OrderDto
        {
            Id = order.Id,
            OrderNumber = order.OrderNumber,
            OrderDate = order.OrderDate.ToString("yyyy-MM-dd"),
            DeliveryDate = order.DeliveryDate.ToString("yyyy-MM-dd"),
            DistributionCentreId = order.DistributionCentreId,
            DistributionCentreName = order.DistributionCentre?.Name ?? string.Empty,
            Source = order.Source,
            Status = order.Status,
            StatusLabel = order.Status.ToString(),
            Notes = order.Notes,
            IsPriceMissing = hasMissing,
            IsPriceMismatch = hasMismatch,
            IsAdjusted = order.IsAdjusted,
            IsValidated = OrderStatusHelper.IsOrderValidated(order),
            TotalValue = order.TotalValue,
            TotalPallets = order.TotalPallets,
            Items = itemDtos
        };
    }

    public async Task<Order?> GetByOrderNumberAsync(string orderNumber)
    {
        return await _dbContext.Orders
            .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber);
    }

    public async Task<OrderDto?> ProcessOrderAsync(int id, CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.Orders
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (order is null)
        {
            return null;
        }

        // Block direct processing of Flagged orders - must be approved first
        if (order.Status == OrderStatus.Flagged)
        {
            throw new InvalidOperationException("Cannot process a flagged order. Resolve pricing issues and approve it first.");
        }

        // Prevent double-processing
        if (order.Status == OrderStatus.Processed)
        {
            throw new InvalidOperationException("This order has already been processed.");
        }

        if (order.Status != OrderStatus.Approved)
        {
            throw new InvalidOperationException($"Order must be in Approved status to process. Current status: {order.Status}.");
        }

        var originalStatus = order.Status;
        var originalNotes = order.Notes;
        foreach (var item in order.Items)
        {
            var planningCheck = await _planningService.CheckStockVsProductionRequirementsAsync(
                item.ProductId,
                item.Quantity,
                order.DeliveryDate,
                cancellationToken);

            if (!planningCheck.IsSufficient)
            {
                var issueText = $"Planning shortfall for product {item.ProductId}: required {planningCheck.RequiredQuantity}, available {planningCheck.AvailableQuantity}.";
                order.Notes = AppendNote(order.Notes, issueText);
            }
        }

        var productIds = order.Items
            .Select(x => x.ProductId)
            .Distinct()
            .ToList();

        var stocksByProductId = await _stockService.GetByProductIdsAsync(productIds, cancellationToken);

        foreach (var item in order.Items)
        {
            if (!stocksByProductId.TryGetValue(item.ProductId, out var stock))
            {
                throw new InvalidOperationException($"Stock not set for product {item.ProductId}.");
            }

            if (stock.Quantity < item.Quantity)
            {
                throw new InvalidOperationException($"Insufficient stock to process order for product {item.ProductId}. Available: {stock.Quantity}, required: {item.Quantity}.");
            }

            _logger.LogInformation(
                "Stock check passed for processing. OrderId={OrderId}, ProductId={ProductId}, CurrentStock={CurrentStock}, RequiredQuantity={RequiredQuantity}. Stock remains unchanged until manual update.",
                order.Id,
                item.ProductId,
                stock.Quantity,
                item.Quantity);
        }

        order.Status = OrderStatus.Processed;
        TrackOrderLevelChanges(order.Id, originalStatus, order.Status, originalNotes, order.Notes, order.IsAdjusted, order.IsAdjusted, order.TotalValue, order.TotalValue, order.TotalPallets, order.TotalPallets);

        Console.WriteLine($"Processing order {order.Id}, new status: {order.Status}");

        await _dbContext.SaveChangesAsync(cancellationToken);
        return await GetOrderByIdAsync(order.Id, cancellationToken);
    }

    public async Task<OrderDto?> ApproveOrderAsync(int id, CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.Orders
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (order is null)
        {
            return null;
        }

        // Allow approval from Flagged or Validated status
        if (order.Status != OrderStatus.Flagged && order.Status != OrderStatus.Validated)
        {
            throw new InvalidOperationException($"Orders can only be approved from Flagged or Validated status. Current status: {order.Status}.");
        }

        var hasPricingIssues = order.Items.Any(i => i.IsPriceMissing || i.IsPriceMismatch);
        _logger.LogInformation("[APPROVAL CHECK] OrderId: {OrderId}, HasPricingIssues: {HasPricingIssues}, Allowed: true", order.Id, hasPricingIssues);

        var originalStatus = order.Status;
        order.Status = OrderStatus.Approved;
        TrackOrderLevelChanges(order.Id, originalStatus, order.Status, order.Notes, order.Notes, order.IsAdjusted, order.IsAdjusted, order.TotalValue, order.TotalValue, order.TotalPallets, order.TotalPallets);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return await GetOrderByIdAsync(order.Id, cancellationToken);
    }

    public async Task<OrderDto?> AdjustOrderAsync(int id, AdjustOrderDto dto, CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.Orders
            .Include(x => x.DistributionCentre)
            .Include(x => x.Items)
                .ThenInclude(x => x.Product)
            .Include(x => x.Items)
                .ThenInclude(x => x.ProductionDecisions)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (order is null)
        {
            return null;
        }

        if (dto.Items.Count == 0)
        {
            throw new InvalidOperationException("At least one adjustment item is required.");
        }

        var duplicateProductIds = dto.Items
            .GroupBy(x => x.ProductId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateProductIds.Count > 0)
        {
            throw new InvalidOperationException($"Duplicate adjustment items found for product IDs: {string.Join(", ", duplicateProductIds)}.");
        }

        var originalStatus = order.Status;
        var originalNotes = order.Notes;
        var originalIsAdjusted = order.IsAdjusted;
        var originalTotalValue = order.TotalValue;
        var originalTotalPallets = order.TotalPallets;
        var quantityChanged = false;

        if (order.DistributionCentre is null)
        {
            var (distributionCentre, distributionCentreWarning) = await ResolveOrCreateDistributionCentreForOrderAsync(
                order.DistributionCentreId,
                order.DistributionCentreId.ToString(CultureInfo.InvariantCulture),
                cancellationToken);
            order.DistributionCentreId = distributionCentre.Id;
            order.DistributionCentre = distributionCentre;
            if (!string.IsNullOrWhiteSpace(distributionCentreWarning))
            {
                Console.WriteLine($"[AdjustOrderAsync] DC warning for OrderId={id}: {distributionCentreWarning}");
            }
        }

        Console.WriteLine($"[AdjustOrderAsync] OrderId={id}, Items received: {dto.Items.Count}");
        foreach (var adjustment in dto.Items)
        {
            Console.WriteLine($"[AdjustOrderAsync] Item: ProductId={adjustment.ProductId}, Qty={adjustment.Quantity}, Price={adjustment.Price}");

            if (adjustment.Quantity <= 0)
            {
                throw new InvalidOperationException($"Invalid quantity for product {adjustment.ProductId}: must be greater than 0.");
            }

            var line = order.Items.FirstOrDefault(x => x.ProductId == adjustment.ProductId);
            if (line is null)
            {
                throw new InvalidOperationException($"Order item for product {adjustment.ProductId} was not found.");
            }

            var oldQty = line.Quantity;
            var oldPrice = line.Price;
            var oldPallets = line.Pallets;
            var oldMissing = line.IsPriceMissing;
            var oldMismatch = line.IsPriceMismatch;

            line.Quantity = adjustment.Quantity;
            quantityChanged |= oldQty != line.Quantity;

            // Safely parse the incoming price — guards against comma-decimal strings (e.g. "48,1")
            // that may survive model binding in non-invariant-culture environments.
            decimal parsedPrice;
            if (adjustment.Price.HasValue)
            {
                var priceString = adjustment.Price.Value.ToString(CultureInfo.InvariantCulture)
                    .Replace(",", ".");
                if (!decimal.TryParse(priceString, NumberStyles.Any, CultureInfo.InvariantCulture, out parsedPrice))
                {
                    throw new InvalidOperationException($"Invalid price format for product {adjustment.ProductId}.");
                }
            }
            else
            {
                parsedPrice = line.Price;
            }
            Console.WriteLine($"[AdjustOrderAsync] Parsed price for product {adjustment.ProductId}: {parsedPrice}");

            var resolvedPrice = Math.Round(parsedPrice, 2);
            if (resolvedPrice <= 0)
            {
                throw new InvalidOperationException($"Invalid price for product {adjustment.ProductId}: must be greater than 0.");
            }

            var effectivePrice = await _pricingService.GetEffectivePriceAsync(line.ProductId, order.DistributionCentreId, null, cancellationToken);
            line.Price = resolvedPrice;
            line.IsPriceMissing = !effectivePrice.IsFound || !effectivePrice.EffectivePrice.HasValue;
            line.IsPriceMismatch = !line.IsPriceMissing
                && Math.Round(effectivePrice.EffectivePrice!.Value, 2) != resolvedPrice;
            line.Pallets = await _palletService.CalculatePalletsAsync(line.ProductId, line.Quantity, cancellationToken);
            if (line.IsPriceMissing)
            {
                Console.WriteLine($"[AdjustOrderAsync] Price missing for product {line.ProductId} ({line.Product?.Name}) at DC {order.DistributionCentreId}.");
            }

            TrackItemChanges(order.Id, line.ProductId, oldQty, line.Quantity, oldPrice, line.Price, oldPallets, line.Pallets, oldMissing, line.IsPriceMissing, oldMismatch, line.IsPriceMismatch);
        }

        order.IsAdjusted = true;
        order.TotalValue = order.Items.Sum(x => x.Quantity * x.Price);
        order.TotalPallets = order.Items.Sum(x => x.Pallets);

        // Notes: overwrite with user-supplied value only — never append system warnings.
        order.Notes = string.IsNullOrWhiteSpace(dto.Notes) ? order.Notes : dto.Notes.Trim();

        // Status driven entirely by current item pricing flags, not legacy Notes content.
        bool hasMissing = order.Items.Any(x => x.IsPriceMissing);
        bool hasMismatch = order.Items.Any(x => x.IsPriceMismatch);
        Console.WriteLine($"[AdjustOrderAsync] OrderId={id}, HasMissing={hasMissing}, HasMismatch={hasMismatch}");

        if (quantityChanged)
        {
            var staleProductionDecisions = order.Items
                .SelectMany(x => x.ProductionDecisions)
                .ToList();

            if (staleProductionDecisions.Count > 0)
            {
                _dbContext.ProductionDecisions.RemoveRange(staleProductionDecisions);
                _logger.LogInformation(
                    "[AdjustOrderAsync] Removed {DecisionCount} stale production decision(s) for OrderId={OrderId} after quantity change.",
                    staleProductionDecisions.Count,
                    order.Id);
            }
        }

        if (hasMissing || hasMismatch)
        {
            order.Status = OrderStatus.Flagged;
        }
        else if (quantityChanged && (originalStatus == OrderStatus.InProduction || originalStatus == OrderStatus.Processed))
        {
            order.Status = OrderStatus.InProduction;
        }
        else if (!quantityChanged && (originalStatus == OrderStatus.InProduction || originalStatus == OrderStatus.Processed))
        {
            order.Status = originalStatus;
        }
        else
        {
            order.Status = OrderStatus.Validated;
        }

        order.Notes = SafeVarchar1000(order.Notes, $"Order[{order.Id}].Notes");
        LogAdjustPreSaveStringLengths(order.Id, order.Notes);

        TrackOrderLevelChanges(order.Id, originalStatus, order.Status, originalNotes, order.Notes, originalIsAdjusted, order.IsAdjusted, originalTotalValue, order.TotalValue, originalTotalPallets, order.TotalPallets);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return await GetOrderByIdAsync(order.Id, cancellationToken);
    }

    public async Task<OrderDto?> RecalculateOrderAsync(int id, CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.Orders
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (order is null)
        {
            return null;
        }

        var originalStatus = order.Status;
        var originalNotes = order.Notes;
        var originalIsAdjusted = order.IsAdjusted;
        var originalTotalValue = order.TotalValue;
        var originalTotalPallets = order.TotalPallets;

        var productIds = order.Items
            .Select(item => item.ProductId)
            .Distinct()
            .ToList();

        var products = await _dbContext.Products
            .AsNoTracking()
            .Where(product => productIds.Contains(product.Id))
            .ToDictionaryAsync(product => product.Id, cancellationToken);

        foreach (var item in order.Items)
        {
            var oldPallets = item.Pallets;

            var conversionRate = products.TryGetValue(item.ProductId, out var product)
                ? product.PalletConversionRate
                : 0m;

            item.Pallets = conversionRate > 0
                ? decimal.Round(item.Quantity / conversionRate, 2)
                : item.Quantity;

            TrackItemChanges(order.Id, item.ProductId, item.Quantity, item.Quantity, item.Price, item.Price, oldPallets, item.Pallets, item.IsPriceMissing, item.IsPriceMissing, item.IsPriceMismatch, item.IsPriceMismatch);
        }

        order.TotalPallets = order.Items.Sum(item => item.Pallets);
        order.TotalValue = order.Items.Sum(item => item.Quantity * item.Price);

        TrackOrderLevelChanges(order.Id, originalStatus, order.Status, originalNotes, order.Notes, originalIsAdjusted, order.IsAdjusted, originalTotalValue, order.TotalValue, originalTotalPallets, order.TotalPallets);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return await GetOrderByIdAsync(order.Id, cancellationToken);
    }

    public async Task SoftDeleteOrderAsync(int id, CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.Orders
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (order is null)
        {
            throw new KeyNotFoundException($"Order not found. OrderId={id}.");
        }

        if ((int)order.Status >= (int)OrderStatus.Processed)
        {
            throw new InvalidOperationException($"Order '{order.OrderNumber}' cannot be deleted because status is '{order.Status}'. Only orders before Processed can be deleted.");
        }

        order.IsActive = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<OrderItemSwapAuditResponseDto> AuditHistoricalSwappedOrderItemsAsync(
        bool onlyConfirmed = false,
        int limit = 500,
        string? orderNumber = null,
        string? sku = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedOrderNumber = string.IsNullOrWhiteSpace(orderNumber) ? null : orderNumber.Trim();
        var normalizedSku = string.IsNullOrWhiteSpace(sku) ? null : sku.Trim();

        var query = _dbContext.OrderItems
            .AsNoTracking()
            .Include(x => x.Order)
            .Include(x => x.Product)
            .Where(x => x.Order != null && x.Order.Source == OrderSource.CSV);

        if (!string.IsNullOrWhiteSpace(normalizedOrderNumber))
        {
            query = query.Where(x => x.Order!.OrderNumber == normalizedOrderNumber);
        }

        if (!string.IsNullOrWhiteSpace(normalizedSku))
        {
            query = query.Where(x =>
                (x.ProductCode ?? string.Empty) == normalizedSku
                || (x.Product != null && x.Product.SKUCode == normalizedSku));
        }

        var rows = await query
            .OrderBy(x => x.OrderId)
            .ThenBy(x => x.Id)
            .Take(Math.Max(1, limit))
            .ToListAsync(cancellationToken);

        var candidates = new List<OrderItemSwapAuditCandidateDto>();
        var confirmedCount = 0;

        foreach (var item in rows)
        {
            var metadata = item.Metadata;
            var hasRawQty = TryGetRawValue(metadata, "QtyRaw", "CsvRawQty", out var qtyRawText, out var qtyRaw);
            var hasRawCostper = TryGetRawValue(metadata, "CostperRaw", "CsvRawCostper", out var costperRawText, out var costperRaw);
            var hasGrossRaw = TryGetRawValue(metadata, "GrossCstRaw", "GrossCst", out var grossRawText, out var grossRaw);
            var hasExetendRaw = TryGetRawValue(metadata, "ExetendCstRaw", "ExetendCst", out var exetendRawText, out var exetendRaw);

            var hasSupplierTotal = TryGetRawValue(metadata, "SupplierLineTotal", "SupplierLineTotalRaw", out _, out var supplierTotal)
                || hasGrossRaw
                || hasExetendRaw;

            if (!hasSupplierTotal)
            {
                supplierTotal = 0m;
            }
            else if (!TryGetRawValue(metadata, "SupplierLineTotal", "SupplierLineTotalRaw", out _, out supplierTotal))
            {
                supplierTotal = hasGrossRaw ? grossRaw : exetendRaw;
            }

            var currentLineTotal = item.Quantity * item.Price;
            var suggestedQty = hasRawQty ? qtyRaw : item.Price;
            var suggestedPrice = hasRawCostper ? costperRaw : item.Quantity;
            var suggestedLineTotal = suggestedQty * suggestedPrice;
            var supplierMatchesSuggested = hasSupplierTotal && NearlyEqual(supplierTotal, suggestedLineTotal);
            var supplierMatchesCurrent = hasSupplierTotal && NearlyEqual(supplierTotal, currentLineTotal);

            var swapByRaw = hasRawQty
                && hasRawCostper
                && !NearlyEqual(qtyRaw, costperRaw)
                && NearlyEqual(item.Quantity, costperRaw)
                && NearlyEqual(item.Price, qtyRaw);

            var suspiciousShape = item.IsCsvPrice
                && item.Quantity > 0
                && item.Price > 0
                && item.Quantity <= 25m
                && item.Price >= 40m
                && item.Price > (item.Quantity * 1.5m);

            var supplierHeuristicConfirmed = !swapByRaw
                && hasSupplierTotal
                && supplierMatchesCurrent
                && suspiciousShape
                && item.Price >= 20m
                && item.Quantity <= 30m;

            var bugSignatureConfirmed = !swapByRaw
                && hasRawQty
                && hasRawCostper
                && NearlyEqual(item.Quantity, qtyRaw)
                && NearlyEqual(item.Price, costperRaw)
                && suspiciousShape
                && (!hasSupplierTotal || !supplierMatchesCurrent);

            var confirmed = (swapByRaw && (!hasSupplierTotal || supplierMatchesSuggested))
                || supplierHeuristicConfirmed
                || bugSignatureConfirmed;

            var finalSuggestedQty = bugSignatureConfirmed ? item.Price : suggestedQty;
            var finalSuggestedPrice = bugSignatureConfirmed ? item.Quantity : suggestedPrice;

            if (!confirmed && !suspiciousShape)
            {
                continue;
            }

            var reason = swapByRaw
                ? "Raw metadata confirms quantity/unit price are reversed."
                : confirmed
                    ? (bugSignatureConfirmed
                        ? "Historical bug-signature detected: persisted and raw fields share swapped shape while supplier total metadata is absent/inconsistent."
                        : "Supplier totals and quantity/unit price shape strongly indicate historical quantity/unit price swap.")
                    : "Suspicious quantity/price shape; raw metadata requires manual review.";

            if (confirmed && hasSupplierTotal && supplierMatchesCurrent && supplierMatchesSuggested)
            {
                reason += " Supplier total equals both orientations due to multiplication symmetry.";
            }

            var candidate = new OrderItemSwapAuditCandidateDto
            {
                OrderId = item.OrderId,
                OrderItemId = item.Id,
                OrderNumber = item.Order?.OrderNumber ?? string.Empty,
                SKU = item.ProductCode ?? item.Product?.SKUCode ?? string.Empty,
                CurrentQuantity = item.Quantity,
                CurrentUnitPrice = item.Price,
                CurrentLineTotal = currentLineTotal,
                QtyRaw = qtyRawText,
                CostperRaw = costperRawText,
                GrossCstRaw = grossRawText,
                ExetendCstRaw = exetendRawText,
                SuggestedQuantity = finalSuggestedQty,
                SuggestedUnitPrice = finalSuggestedPrice,
                DetectionReason = reason,
                IsConfirmedSwap = confirmed
            };

            _logger.LogInformation(
                "[ORDER ITEM AUDIT] OrderNumber: {OrderNumber}, SKU: {Sku}, CurrentQty: {CurrentQty}, CurrentUnitPrice: {CurrentUnitPrice}, CurrentLineTotal: {CurrentLineTotal}, QtyRaw: {QtyRaw}, CostperRaw: {CostperRaw}, SuggestedQty: {SuggestedQty}, SuggestedUnitPrice: {SuggestedUnitPrice}, Confirmed: {Confirmed}, Reason: {Reason}",
                candidate.OrderNumber,
                candidate.SKU,
                candidate.CurrentQuantity,
                candidate.CurrentUnitPrice,
                candidate.CurrentLineTotal,
                candidate.QtyRaw,
                candidate.CostperRaw,
                candidate.SuggestedQuantity,
                candidate.SuggestedUnitPrice,
                candidate.IsConfirmedSwap,
                candidate.DetectionReason);

            if (candidate.IsConfirmedSwap)
            {
                confirmedCount++;
                _logger.LogWarning(
                    "[ORDER ITEM SWAP CONFIRMED] OrderNumber: {OrderNumber}, SKU: {Sku}, CurrentQty: {CurrentQty}, CurrentUnitPrice: {CurrentUnitPrice}, SuggestedQty: {SuggestedQty}, SuggestedUnitPrice: {SuggestedUnitPrice}",
                    candidate.OrderNumber,
                    candidate.SKU,
                    candidate.CurrentQuantity,
                    candidate.CurrentUnitPrice,
                    candidate.SuggestedQuantity,
                    candidate.SuggestedUnitPrice);
            }

            candidates.Add(candidate);
        }

        var outputItems = onlyConfirmed
            ? candidates.Where(x => x.IsConfirmedSwap).ToList()
            : candidates;

        return new OrderItemSwapAuditResponseDto
        {
            TotalScanned = rows.Count,
            TotalCandidates = candidates.Count,
            TotalConfirmed = confirmedCount,
            Items = outputItems
        };
    }

    public async Task<OrderItemSwapRepairResponseDto> RepairHistoricalSwappedOrderItemsAsync(
        bool dryRun = true,
        int limit = 500,
        string? orderNumber = null,
        string? sku = null,
        CancellationToken cancellationToken = default)
    {
        var audit = await AuditHistoricalSwappedOrderItemsAsync(false, limit, orderNumber, sku, cancellationToken);
        var confirmed = audit.Items.Where(x => x.IsConfirmedSwap).ToList();

        var response = new OrderItemSwapRepairResponseDto
        {
            DryRun = dryRun,
            TotalScanned = audit.TotalScanned,
            TotalCandidates = audit.TotalCandidates,
            TotalConfirmed = audit.TotalConfirmed
        };

        if (confirmed.Count == 0)
        {
            return response;
        }

        if (dryRun)
        {
            foreach (var item in confirmed)
            {
                _logger.LogInformation(
                    "[ORDER ITEM REPAIR SKIPPED] DryRun: true, OrderNumber: {OrderNumber}, SKU: {Sku}, CurrentQty: {CurrentQty}, CurrentUnitPrice: {CurrentUnitPrice}, SuggestedQty: {SuggestedQty}, SuggestedUnitPrice: {SuggestedUnitPrice}",
                    item.OrderNumber,
                    item.SKU,
                    item.CurrentQuantity,
                    item.CurrentUnitPrice,
                    item.SuggestedQuantity,
                    item.SuggestedUnitPrice);
                response.SkippedItems.Add(item);
            }

            response.SkippedCount = response.SkippedItems.Count;
            return response;
        }

        await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var itemIds = confirmed.Select(x => x.OrderItemId).ToHashSet();
            var dbItems = await _dbContext.OrderItems
                .Include(x => x.Order)
                .Include(x => x.Product)
                .Where(x => itemIds.Contains(x.Id))
                .ToListAsync(cancellationToken);

            foreach (var dbItem in dbItems)
            {
                var confirmedCandidate = confirmed.First(x => x.OrderItemId == dbItem.Id);
                var beforeQty = dbItem.Quantity;
                var beforePrice = dbItem.Price;

                dbItem.Quantity = confirmedCandidate.SuggestedQuantity;
                dbItem.Price = confirmedCandidate.SuggestedUnitPrice;

                _logger.LogWarning(
                    "[ORDER ITEM REPAIR] OrderNumber: {OrderNumber}, SKU: {Sku}, OldQty: {OldQty}, OldUnitPrice: {OldUnitPrice}, NewQty: {NewQty}, NewUnitPrice: {NewUnitPrice}, NewLineTotal: {NewLineTotal}",
                    dbItem.Order?.OrderNumber ?? string.Empty,
                    dbItem.ProductCode ?? dbItem.Product?.SKUCode ?? string.Empty,
                    beforeQty,
                    beforePrice,
                    dbItem.Quantity,
                    dbItem.Price,
                    dbItem.Quantity * dbItem.Price);

                response.RepairedItems.Add(confirmedCandidate);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            response.RepairedCount = response.RepairedItems.Count;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }

        return response;
    }

    private async Task<(decimal Total, decimal TotalPallets, bool HasPricingIssues, List<string> Warnings)> BuildOrderItemsAsync(
        Order order,
        List<OrderItemCreateDto> items,
        DistributionCentre distributionCentre,
        bool useProvidedPrices,
        CancellationToken cancellationToken)
    {
        decimal total = 0;
        decimal totalPallets = 0;
        var warnings = new List<string>();
        var missingPricingProducts = new HashSet<int>();

        var requestedProductIds = items
            .Select(x => x.ProductId)
            .Distinct()
            .ToList();

        var products = await _dbContext.Products
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(x => requestedProductIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        foreach (var item in items)
        {
            if (!products.TryGetValue(item.ProductId, out var product))
            {
                throw new InvalidOperationException($"Product with ID {item.ProductId} does not exist.");
            }

            decimal poPrice;
            decimal? expectedPrice = null;
            bool isPriceMissing;
            bool isMismatch;
            bool isCsvPrice;
            EffectivePriceResult? priceLookup = null;

            if (useProvidedPrices)
            {
                if (!item.Price.HasValue)
                {
                    throw new InvalidOperationException("Price is required");
                }

                if (item.Price.Value <= 0)
                {
                    throw new InvalidOperationException("Price must be greater than 0");
                }

                poPrice = item.Price.Value;
                priceLookup = await _pricingService.GetEffectivePriceAsync(item.ProductId, distributionCentre.Id, null, cancellationToken);
                isPriceMissing = !priceLookup.IsFound || !priceLookup.EffectivePrice.HasValue;
                expectedPrice = priceLookup.EffectivePrice;
                isMismatch = expectedPrice.HasValue
                    && Math.Round(poPrice, 2) != Math.Round(expectedPrice.Value, 2);
                isCsvPrice = true;
            }
            else
            {
                if (!item.Price.HasValue)
                {
                    throw new InvalidOperationException("Price is required");
                }

                if (item.Price.Value <= 0)
                {
                    throw new InvalidOperationException("Price must be greater than 0");
                }

                poPrice = item.Price.Value;
                priceLookup = await _pricingService.GetEffectivePriceAsync(item.ProductId, distributionCentre.Id, null, cancellationToken);
                isPriceMissing = !priceLookup.IsFound || !priceLookup.EffectivePrice.HasValue;
                expectedPrice = priceLookup.EffectivePrice;
                isMismatch = expectedPrice.HasValue
                    && Math.Round(poPrice, 2) != Math.Round(expectedPrice.Value, 2);
                isCsvPrice = false;
            }

            if (isPriceMissing)
            {
                missingPricingProducts.Add(product.Id);
                _logger.LogInformation("[CSV] Using CSV price for: {SkuCode} (Needs Pricing)", product.SKUCode);
            }

            var pallets = await _palletService.CalculatePalletsAsync(item.ProductId, item.Quantity, cancellationToken);

            var createdEntity = new OrderItem
            {
                ProductId = item.ProductId,
                ProductCode = item.IsUnmapped
                    ? CleanProductInput(item.ProductCode ?? string.Empty)
                    : (string.IsNullOrWhiteSpace(item.ProductCode) ? CleanProductInput(product.SKUCode) : CleanProductInput(item.ProductCode)),
                ProductName = item.IsUnmapped
                    ? CleanProductInput(item.ProductName ?? string.Empty)
                    : (string.IsNullOrWhiteSpace(item.ProductName) ? CleanProductInput(product.Name) : CleanProductInput(item.ProductName)),
                Quantity = item.Quantity,
                Price = poPrice,
                Pallets = pallets,
                IsUnmapped = item.IsUnmapped || !product.IsMapped,
                IsPriceMissing = isPriceMissing,
                IsPriceMismatch = isMismatch,
                IsCsvPrice = isCsvPrice,
                Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
            };

            order.Items.Add(createdEntity);
            _logger.LogInformation(
                "[BACKEND ITEM ENTITY] SKU: {Sku}, RawQty: {RawQty}, RawCostper: {RawCostper}, ResolvedQuantity: {ResolvedQuantity}, ResolvedPrice: {ResolvedPrice}, LineTotal: {LineTotal}",
                createdEntity.ProductCode ?? product.SKUCode ?? string.Empty,
                createdEntity.Metadata.TryGetValue("CsvRawQty", out var rawQty) ? rawQty : string.Empty,
                createdEntity.Metadata.TryGetValue("CsvRawCostper", out var rawCostper) ? rawCostper : string.Empty,
                createdEntity.Quantity,
                createdEntity.Price,
                createdEntity.Quantity * createdEntity.Price);

            if (IsQtyCostperSwap(createdEntity.Metadata, createdEntity.Quantity, createdEntity.Price))
            {
                _logger.LogError(
                    "[BACKEND SWAP DETECTED] Stage: {Stage}, SKU: {Sku}, RawQty: {RawQty}, RawCostper: {RawCostper}, Quantity: {Quantity}, UnitPrice: {UnitPrice}",
                    "OrderItemEntity",
                    createdEntity.ProductCode ?? product.SKUCode ?? string.Empty,
                    createdEntity.Metadata.TryGetValue("CsvRawQty", out var entityRawQty) ? entityRawQty : string.Empty,
                    createdEntity.Metadata.TryGetValue("CsvRawCostper", out var entityRawCostper) ? entityRawCostper : string.Empty,
                    createdEntity.Quantity,
                    createdEntity.Price);
            }

            total += item.Quantity * poPrice;
            totalPallets += pallets;
        }

        if (missingPricingProducts.Count > 0)
        {
            warnings.Add(BuildGroupedMissingPriceWarning(missingPricingProducts.Count));
        }

        bool hasMissing = order.Items.Any(i => i.IsPriceMissing);
        bool hasMismatch = order.Items.Any(i => i.IsPriceMismatch);

        Console.WriteLine($"Order {order.Id} - Missing: {hasMissing}, Mismatch: {hasMismatch}");

        order.Status = (hasMissing || hasMismatch)
            ? OrderStatus.Flagged
            : OrderStatus.Validated;

        return (total, totalPallets, hasMissing || hasMismatch, warnings);
    }

    private void TrackItemChanges(int orderId, int productId, decimal oldQty, decimal newQty, decimal oldPrice, decimal newPrice, decimal oldPallets, decimal newPallets, bool oldMissing, bool newMissing, bool oldMismatch, bool newMismatch)
    {
        if (oldQty != newQty)
        {
            _auditService.TrackChange("Order", orderId, $"Item[{productId}].Quantity", FormatDecimal(oldQty), FormatDecimal(newQty));
        }

        if (oldPrice != newPrice)
        {
            _auditService.TrackChange("Order", orderId, $"Item[{productId}].Price", FormatCurrency(oldPrice), FormatCurrency(newPrice));
        }

        if (oldPallets != newPallets)
        {
            _auditService.TrackChange("Order", orderId, $"Item[{productId}].Pallets", FormatDecimal(oldPallets), FormatDecimal(newPallets));
        }

        if (oldMissing != newMissing)
        {
            _auditService.TrackChange("Order", orderId, $"Item[{productId}].IsPriceMissing", oldMissing.ToString(), newMissing.ToString());
        }

        if (oldMismatch != newMismatch)
        {
            _auditService.TrackChange("Order", orderId, $"Item[{productId}].IsPriceMismatch", oldMismatch.ToString(), newMismatch.ToString());
        }
    }

    private void TrackOrderLevelChanges(int orderId, OrderStatus originalStatus, OrderStatus newStatus, string? originalNotes, string? newNotes, bool originalIsAdjusted, bool newIsAdjusted, decimal originalTotalValue, decimal newTotalValue, decimal originalTotalPallets, decimal newTotalPallets)
    {
        if (originalStatus != newStatus)
        {
            _auditService.TrackChange("Order", orderId, "Status", originalStatus.ToString(), newStatus.ToString());
        }

        if (!string.Equals(originalNotes, newNotes, StringComparison.Ordinal))
        {
            _auditService.TrackChange("Order", orderId, "Notes", originalNotes, newNotes);
        }

        if (originalIsAdjusted != newIsAdjusted)
        {
            _auditService.TrackChange("Order", orderId, "IsAdjusted", originalIsAdjusted.ToString(), newIsAdjusted.ToString());
        }

        if (originalTotalValue != newTotalValue)
        {
            _auditService.TrackChange("Order", orderId, "TotalValue", FormatCurrency(originalTotalValue), FormatCurrency(newTotalValue));
        }

        if (originalTotalPallets != newTotalPallets)
        {
            _auditService.TrackChange("Order", orderId, "TotalPallets", FormatDecimal(originalTotalPallets), FormatDecimal(newTotalPallets));
        }
    }

    private static string AppendNote(string? current, string addition)
    {
        if (string.IsNullOrWhiteSpace(addition))
        {
            return current ?? string.Empty;
        }

        return string.IsNullOrWhiteSpace(current) ? addition : $"{current} | {addition}";
    }

    private static string? SafeVarchar1000(string? value, string fieldName)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (value.Length <= 1000)
        {
            return value;
        }

        Console.WriteLine($"[AdjustOrderAsync] Truncating {fieldName} from length {value.Length} to 1000.");
        return value[..1000];
    }

    private void LogAdjustPreSaveStringLengths(int orderId, string? notes)
    {
        Console.WriteLine($"[AdjustOrderAsync][PreSave] OrderId={orderId}, Notes length={notes?.Length ?? 0}");

        var pendingAuditLogs = _dbContext.ChangeTracker
            .Entries<AuditLog>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity)
            .ToList();

        foreach (var audit in pendingAuditLogs)
        {
            Console.WriteLine($"[AdjustOrderAsync][PreSave] Audit entity={audit.Entity}, entityId={audit.EntityId}, field={audit.Field}, OldValue length={audit.OldValue?.Length ?? 0}, NewValue length={audit.NewValue?.Length ?? 0}");
        }
    }

    private static string FormatDecimal(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatCurrency(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static DateTime ToDbDate(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

    private OrderDto MapOrderToDto(Order order)
    {
        Console.WriteLine($"Order {order.Id} status: {order.Status}");

        bool hasMissing = order.Items.Any(i => i.IsPriceMissing);
        bool hasMismatch = order.Items.Any(i => i.IsPriceMismatch);
        Console.WriteLine($"DTO -> Order {order.Id}: Missing={hasMissing}, Mismatch={hasMismatch}");

        return new OrderDto
        {
            Id = order.Id,
            OrderNumber = order.OrderNumber,
            OrderDate = order.OrderDate.ToString("yyyy-MM-dd"),
            DeliveryDate = order.DeliveryDate.ToString("yyyy-MM-dd"),
            DistributionCentreId = order.DistributionCentreId,
            DistributionCentreName = order.DistributionCentre?.Name ?? string.Empty,
            Source = order.Source,
            Status = order.Status,
            StatusLabel = order.Status.ToString(),
            Notes = order.Notes,
            IsPriceMissing = hasMissing,
            IsPriceMismatch = hasMismatch,
            IsAdjusted = order.IsAdjusted,
            IsValidated = OrderStatusHelper.IsOrderValidated(order),
            TotalValue = order.TotalValue,
            TotalPallets = order.TotalPallets,
            Items = order.Items.Select(x =>
            {
                var dto = new OrderItemDto
                {
                    Id = x.Id,
                    ProductId = x.ProductId,
                    ProductName = x.ProductName ?? x.Product?.Name ?? string.Empty,
                    ProductCode = x.ProductCode ?? x.Product?.SKUCode ?? string.Empty,
                    SKUCode = x.ProductCode ?? x.Product?.SKUCode ?? string.Empty,
                    Quantity = x.Quantity,
                    Price = x.Price,
                    Pallets = x.Pallets,
                    LineTotal = x.Quantity * x.Price,
                    IsUnmapped = x.IsUnmapped || x.Product?.IsMapped == false,
                    IsPriceMissing = x.IsPriceMissing,
                    IsPriceMismatch = x.IsPriceMismatch,
                    IsCsvPrice = x.IsCsvPrice
                };

                _logger.LogInformation(
                    "[BACKEND ITEM DTO] SKU: {Sku}, RawQty: {RawQty}, RawCostper: {RawCostper}, ResolvedQuantity: {ResolvedQuantity}, ResolvedPrice: {ResolvedPrice}, LineTotal: {LineTotal}",
                    dto.SKUCode,
                    x.Metadata.TryGetValue("CsvRawQty", out var rawQty) ? rawQty : string.Empty,
                    x.Metadata.TryGetValue("CsvRawCostper", out var rawCostper) ? rawCostper : string.Empty,
                    dto.Quantity,
                    dto.Price,
                    dto.LineTotal);

                if (IsQtyCostperSwap(x.Metadata, dto.Quantity, dto.Price))
                {
                    _logger.LogError(
                        "[BACKEND SWAP DETECTED] Stage: {Stage}, SKU: {Sku}, RawQty: {RawQty}, RawCostper: {RawCostper}, Quantity: {Quantity}, UnitPrice: {UnitPrice}",
                        "OrderItemDto",
                        dto.SKUCode,
                        x.Metadata.TryGetValue("CsvRawQty", out var dtoRawQty) ? dtoRawQty : string.Empty,
                        x.Metadata.TryGetValue("CsvRawCostper", out var dtoRawCostper) ? dtoRawCostper : string.Empty,
                        dto.Quantity,
                        dto.Price);
                }

                return dto;
            }).ToList()
        };
    }
}
