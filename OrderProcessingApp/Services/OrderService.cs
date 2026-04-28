using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;
using OrderProcessingApp.Options;
using System.Globalization;

namespace OrderProcessingApp.Services;

public class OrderService : IOrderService
{
    private readonly AppDbContext _dbContext;
    private readonly IPricingService _pricingService;
    private readonly IPalletService _palletService;
    private readonly IPlanningService _planningService;
    private readonly IAuditService _auditService;
    private readonly ILogger<OrderService> _logger;
    private readonly CsvImportOptions _csvImportOptions;

    public OrderService(
        AppDbContext dbContext,
        IPricingService pricingService,
        IPalletService palletService,
        IPlanningService planningService,
        IAuditService auditService,
        IOptions<CsvImportOptions> csvImportOptions,
        ILogger<OrderService> logger)
    {
        _dbContext = dbContext;
        _pricingService = pricingService;
        _palletService = palletService;
        _planningService = planningService;
        _auditService = auditService;
        _csvImportOptions = csvImportOptions.Value;
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

        var distributionCentres = await _dbContext.DistributionCentres
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var products = await _dbContext.Products
            .ToListAsync(cancellationToken);

        var validRows = new List<ValidatedCsvRow>();

        foreach (var row in rows)
        {
            try
            {
                var orderNumber = row.OrderNumber.Trim();
                var distributionCentreInput = row.DistributionCentre.Trim();
                var productCodeInput = CleanProductInput(row.ProductCode);
                var productNameInput = CleanProductInput(row.ProductName);
                var productInput = GetResolvedProductInput(row);

                if (string.IsNullOrWhiteSpace(orderNumber))
                {
                    AddValidationError(result, row, "Order number is required.", "OrderNumber");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(distributionCentreInput))
                {
                    AddValidationError(result, row, "Distribution centre is required.", "DistributionCentre");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(productInput))
                {
                    AddValidationError(result, row, "Product is required.", "Product");
                    continue;
                }

                if (row.Price <= 0)
                {
                    AddValidationError(result, row, "Price must be greater than 0", "Price");
                    continue;
                }

                var distributionCentre = ResolveDistributionCentre(distributionCentres, distributionCentreInput);
                if (distributionCentre is null)
                {
                    Console.WriteLine($"Resolving DistributionCentreId failed for '{distributionCentreInput}'.");
                    LogDistributionCentreMismatch(distributionCentreInput, distributionCentres);
                    AddMissingDistributionCentre(result, row.DistributionCentre);
                    pendingRows.Add(CloneRow(row));
                    continue;
                }

                Console.WriteLine($"Resolving DistributionCentreId for '{distributionCentreInput}' -> {distributionCentre.Id}");

                var product = ResolveProduct(products, productCodeInput, productNameInput, productInput);
                if (product is null)
                {
                    product = await CreatePlaceholderProductAsync(row, products, cancellationToken);
                }

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
                AddValidationError(result, row, $"Unexpected processing error: {exception.Message}");
                _logger.LogWarning(exception, "CSV import failed during row validation for file {FileName} row {RowNumber}", row.FileName, row.RowNumber);
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
                    Quantity = validRow.Row.Quantity,
                    Price = validRow.Row.Price,
                    IsUnmapped = !validRow.Product.IsMapped,
                    Metadata = new Dictionary<string, string>(validRow.Row.Metadata, StringComparer.OrdinalIgnoreCase)
                }).ToList();

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
                await _dbContext.SaveChangesAsync(cancellationToken);
                result.CreatedOrders++;
                if (order.Status == OrderStatus.Flagged)
                {
                    result.FlaggedOrders++;
                }
            }
            catch (Exception exception)
            {
                result.SkippedOrders++;
                foreach (var row in group)
                {
                    AddValidationError(result, row.Row, $"Unexpected processing error for order '{orderNumber}': {exception.Message}");
                }

                _logger.LogWarning(exception, "CSV import failed while creating order {OrderNumber}", orderNumber);
                _dbContext.ChangeTracker.Clear();
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
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var existingNames = existingCentres
            .Select(dc => Normalize(dc.Name))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);

        var result = new CreateMissingDistributionCentresResultDto
        {
            DistributionCentreId = dto.DistributionCentreId ?? 0
        };

        foreach (var centreName in requestedCentres)
        {
            var normalizedCentreName = Normalize(centreName);
            var exists = !string.IsNullOrWhiteSpace(normalizedCentreName) && existingNames.Contains(normalizedCentreName);
            if (exists)
            {
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
            existingNames.Add(normalizedCentreName!);
            result.CreatedCentres.Add(centreName);
        }

        if (result.CreatedCentres.Count > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
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

    private DistributionCentre? ResolveDistributionCentre(IEnumerable<DistributionCentre> distributionCentres, string input)
    {
        var normalizedInput = Normalize(input);

        if (string.IsNullOrWhiteSpace(normalizedInput))
            return null;

        var aliasTarget = ResolveDistributionCentreAlias(normalizedInput);
        if (!string.IsNullOrWhiteSpace(aliasTarget))
        {
            _logger.LogInformation(
                "Applied distribution centre alias. CSV value: [{CsvValue}] Alias target: [{AliasTarget}]",
                input,
                aliasTarget);

            var aliasMatch = ResolveDistributionCentreCore(distributionCentres, aliasTarget);
            if (aliasMatch is not null)
                return aliasMatch;
        }

        return ResolveDistributionCentreCore(distributionCentres, input);
    }

    private static DistributionCentre? ResolveDistributionCentreCore(IEnumerable<DistributionCentre> distributionCentres, string input)
    {
        var normalizedInput = Normalize(input);

        if (string.IsNullOrWhiteSpace(normalizedInput))
            return null;

        var exactMatch = distributionCentres.FirstOrDefault(dc =>
            Normalize(dc.Name) == normalizedInput ||
            Normalize(dc.Code) == normalizedInput);
        if (exactMatch is not null)
            return exactMatch;

        return distributionCentres.FirstOrDefault(dc =>
        {
            var normalizedName = Normalize(dc.Name);
            var normalizedCode = Normalize(dc.Code);
            return normalizedName.Contains(normalizedInput, StringComparison.Ordinal)
                || normalizedCode.Contains(normalizedInput, StringComparison.Ordinal);
        });
    }

    private string? ResolveDistributionCentreAlias(string normalizedInput)
    {
        if (_csvImportOptions.DistributionCentreAliases.Count == 0)
            return null;

        foreach (var alias in _csvImportOptions.DistributionCentreAliases)
        {
            if (Normalize(alias.Key) == normalizedInput)
                return alias.Value;
        }

        return null;
    }

    private void LogDistributionCentreMismatch(string input, IEnumerable<DistributionCentre> distributionCentres)
    {
        var normalizedInput = Normalize(input);
        var candidates = distributionCentres
            .Select(dc => $"{dc.Name}/{dc.Code} => {Normalize(dc.Name)}/{Normalize(dc.Code)}")
            .ToArray();
        var aliases = _csvImportOptions.DistributionCentreAliases
            .Select(alias => $"{alias.Key} => {alias.Value}")
            .ToArray();

        _logger.LogWarning(
            "CSV distribution centre not matched. CSV value: [{CsvValue}] Normalized: [{NormalizedValue}] Db values: [{Candidates}] Aliases: [{Aliases}]",
            input,
            normalizedInput,
            string.Join(" | ", candidates),
            string.Join(" | ", aliases));
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

    private async Task<Product> CreatePlaceholderProductAsync(CsvOrderRowDto row, List<Product> products, CancellationToken cancellationToken)
    {
        var productCode = NormalizeProductCode(row.ProductCode);
        var productName = CleanProductInput(row.ProductName);
        var fallbackProduct = GetResolvedProductInput(row);
        var skuCode = !string.IsNullOrWhiteSpace(productCode) ? productCode : fallbackProduct;
        var name = !string.IsNullOrWhiteSpace(productName)
            ? productName
            : $"UNMAPPED PRODUCT - {skuCode}";

        var normalizedSkuCode = NormalizeProductCode(skuCode);
        var existing = ResolveProductBySku(products, normalizedSkuCode)
            ?? await _dbContext.Products.FirstOrDefaultAsync(
                product => product.SKUCode != null && product.SKUCode.Trim().ToLower() == normalizedSkuCode,
                cancellationToken)
            ?? ResolveProduct(products, productCode, productName, fallbackProduct);
        if (existing is not null)
        {
            if (!products.Any(product => product.Id == existing.Id))
            {
                products.Add(existing);
            }

            return existing;
        }

        var entity = new Product
        {
            SKUCode = normalizedSkuCode,
            Name = name,
            PalletConversionRate = 1m,
            IsMapped = false,
            RequiresAttention = true,
            CreatedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified)
        };

        _dbContext.Products.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        products.Add(entity);

        return entity;
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

        return orders.Select(MapOrderToDto).ToList();
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

        return orders.Select(MapOrderToDto).ToList();
    }

    public async Task<OrderDto?> GetOrderByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.Orders
            .AsNoTracking()
            .Include(x => x.DistributionCentre)
            .Include(x => x.Items)
                .ThenInclude(x => x.Product)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        return order is null ? null : MapOrderToDto(order);
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

        // Enforce workflow: only Approved or Validated statuses can proceed to Processed
        var effectiveStatus = OrderStatusHelper.GetEffectiveStatus(order);
        if (effectiveStatus != OrderStatus.Approved)
        {
            throw new InvalidOperationException($"Order must be in Approved status to process. Current status: {order.Status} (effective: {effectiveStatus}).");
        }

        // Prevent double-processing
        if (order.Status == OrderStatus.Processed)
        {
            throw new InvalidOperationException("This order has already been processed.");
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
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (order is null)
        {
            return null;
        }

        if (order.Status == OrderStatus.Processed)
        {
            throw new InvalidOperationException("Processed orders cannot be adjusted.");
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

            var priceLookup = await _pricingService.GetPriceAsync(line.ProductId, order.DistributionCentreId, cancellationToken);
            line.Price = resolvedPrice;
            line.IsPriceMissing = !priceLookup.IsFound || !priceLookup.Price.HasValue;
            line.IsPriceMismatch = !line.IsPriceMissing
                && Math.Round(priceLookup.Price!.Value, 2) != resolvedPrice;
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
        order.Status = (hasMissing || hasMismatch) ? OrderStatus.Flagged : OrderStatus.Validated;

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
            PriceLookupResult? priceLookup = null;

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
                priceLookup = await _pricingService.GetPriceAsync(item.ProductId, distributionCentre.Id, cancellationToken);
                isPriceMissing = !priceLookup.IsFound || !priceLookup.Price.HasValue;
                expectedPrice = priceLookup.Price;
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
                priceLookup = await _pricingService.GetPriceAsync(item.ProductId, distributionCentre.Id, cancellationToken);
                isPriceMissing = !priceLookup.IsFound || !priceLookup.Price.HasValue;
                expectedPrice = priceLookup.Price;
                isMismatch = expectedPrice.HasValue
                    && Math.Round(poPrice, 2) != Math.Round(expectedPrice.Value, 2);
                isCsvPrice = false;
            }

            if (isPriceMissing)
            {
                missingPricingProducts.Add(product.Id);
            }

            var pallets = await _palletService.CalculatePalletsAsync(item.ProductId, item.Quantity, cancellationToken);

            order.Items.Add(new OrderItem
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
            });

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

    private static OrderDto MapOrderToDto(Order order)
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
            Items = order.Items.Select(x => new OrderItemDto
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
            }).ToList()
        };
    }
}
