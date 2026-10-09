using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;

namespace OrderProcessingApp.Services;

public class ReportService : IReportService
{
    private readonly AppDbContext _dbContext;
    private readonly DeliveryClock? _clock;

    public ReportService(AppDbContext dbContext, DeliveryClock? clock = null)
    {
        _dbContext = dbContext;
        _clock = clock;
    }

    public async Task<List<ReportAvailableDateDto>> GetAvailableReportDatesAsync(CancellationToken cancellationToken = default)
    {
        var orderDates = await _dbContext.Orders
            .AsNoTracking()
            .Where(x => x.DeliveryDate.HasValue)
            .Select(x => x.DeliveryDate!.Value.Date)
            .Distinct()
            .ToListAsync(cancellationToken);

        var deliveryDates = await _dbContext.DeliverySchedules
            .AsNoTracking()
            .Select(x => x.DeliveryDate.Date)
            .Distinct()
            .ToListAsync(cancellationToken);

        var pastelDates = await _dbContext.Orders
            .AsNoTracking()
            .Where(x => OrderWorkflowStatusRules.InvoiceExportStatuses.Contains(x.Status))
            .Where(x => x.DeliveryDate.HasValue)
            .Select(x => x.DeliveryDate!.Value.Date)
            .Distinct()
            .ToListAsync(cancellationToken);

        var orderDateSet = orderDates.ToHashSet();
        var deliveryDateSet = deliveryDates.ToHashSet();
        var pastelDateSet = pastelDates.ToHashSet();

        var allDates = orderDateSet
            .Union(deliveryDateSet)
            .OrderByDescending(x => x)
            .ToList();

        return allDates.Select(date =>
        {
            var hasOrders = orderDateSet.Contains(date);
            var hasDeliveryRows = deliveryDateSet.Contains(date);
            var hasSummaryRows = hasOrders;
            var hasExportableRows = hasOrders || hasDeliveryRows || pastelDateSet.Contains(date);

            return new ReportAvailableDateDto
            {
                Date = date.ToString("yyyy-MM-dd"),
                HasOrders = hasOrders,
                HasDeliveryRows = hasDeliveryRows,
                HasSummaryRows = hasSummaryRows,
                HasExportableRows = hasExportableRows
            };
        }).ToList();
    }

    public async Task<ReportSummaryDto> GetSummaryByDeliveryDateAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        return await GetSummaryByDeliveryDateRangeAsync(date, date, cancellationToken);
    }

    public async Task<ReportSummaryDto> GetSummaryByDeliveryDateRangeAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default)
    {
        var start = DateTime.SpecifyKind(fromDate.Date, DateTimeKind.Unspecified);
        var endDateInclusive = DateTime.SpecifyKind(toDate.Date, DateTimeKind.Unspecified);
        if (start > endDateInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(fromDate), "From date must be on or before To date.");
        }

        var end = endDateInclusive.AddDays(1);

        var orders = await _dbContext.Orders
            .AsNoTracking()
            .Include(x => x.DistributionCentre)
            .Include(x => x.Items)
                .ThenInclude(x => x.Product)
            .Where(x => x.DeliveryDate >= start && x.DeliveryDate < end)
            .ToListAsync(cancellationToken);

        var orderComputedStatuses = orders.Select(order => new
        {
            Order = order,
            Status = order.Status.ToString(),
            TotalValue = order.Items.Sum(i => i.Quantity * i.Price)
        }).ToList();

        var allItems = orders.SelectMany(x => x.Items).ToList();

        return new ReportSummaryDto
        {
            BusinessTimeZone = _clock?.BusinessTimeZone.Id ?? "Africa/Johannesburg",
            TotalOrders = orders.Count,
            TotalValue = allItems.Sum(i => i.Quantity * i.Price),
            OrdersByStatus = Enum.GetValues<OrderStatus>()
                .Select(status => new ReportStatusCountDto
                {
                    Status = status.ToString(),
                    Count = orderComputedStatuses.Count(x => x.Order.Status == status),
                    TotalValue = orderComputedStatuses.Where(x => x.Order.Status == status).Sum(x => x.TotalValue)
                })
                .OrderBy(x => x.Status)
                .ToList(),
            SalesByProduct = allItems
                .GroupBy(i =>
                    !string.IsNullOrWhiteSpace(i.ProductName) ? i.ProductName! :
                    !string.IsNullOrWhiteSpace(i.Product?.Name) ? i.Product!.Name :
                    !string.IsNullOrWhiteSpace(i.ProductCode) ? i.ProductCode! :
                    !string.IsNullOrWhiteSpace(i.Product?.SKUCode) ? i.Product!.SKUCode :
                    "Unknown Product")
                .Select(g => new ReportSalesByProductSummaryDto
                {
                    Product = g.Key,
                    Quantity = g.Sum(x => x.Quantity),
                    Value = g.Sum(x => x.Quantity * x.Price)
                })
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Product)
                .ToList(),
            DeliverySummary = orderComputedStatuses
                .Select(x => new ReportDeliverySummaryDto
                {
                    Id = x.Order.Id,
                    PoNumber = x.Order.OrderNumber,
                    Supplier = ResolveSupplier(x.Order),
                    Dc = x.Order.DistributionCentre?.Name ?? string.Empty,
                    DeliveryDate = x.Order.DeliveryDate!.Value.ToString("yyyy-MM-dd"),
                    Status = x.Status,
                    OriginalCsvDeliveryDate = x.Order.OriginalCsvDeliveryDate?.ToString("yyyy-MM-dd"),
                    EnRouteAtUtc = x.Order.EnRouteAtUtc,
                    ExpectedDeliveryDurationHours = x.Order.ExpectedDeliveryDurationHours,
                    ExpectedDeliveryAtUtc = x.Order.ExpectedDeliveryAtUtc,
                    DeliveredAtUtc = x.Order.DeliveredAtUtc,
                    IsDeliveryEstimated = x.Order.IsDeliveryEstimated
                })
                .OrderBy(x => x.Dc)
                .ThenBy(x => x.PoNumber)
                .ToList(),
            DeliveryBreakdown = orders
                .GroupBy(order => new { Supplier = ResolveSupplier(order), Dc = order.DistributionCentre?.Name ?? string.Empty, order.Status })
                .Select(group => new ReportDeliveryBreakdownDto
                {
                    Supplier = group.Key.Supplier,
                    Dc = group.Key.Dc,
                    Status = group.Key.Status.ToString(),
                    Count = group.Count()
                }).OrderBy(row => row.Supplier).ThenBy(row => row.Dc).ThenBy(row => row.Status).ToList()
        };
    }

    public async Task<List<SupplierSummaryGroupDto>> GetSupplierSummaryAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"Generating report for date {date:yyyy-MM-dd}");
        var dbDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        Console.WriteLine($"[REPORT QUERY] Using ScheduledDate: {dbDate:yyyy-MM-dd}");
        var start = dbDate.Date;
        var end = start.AddDays(1);

        var schedules = await _dbContext.DeliverySchedules
            .AsNoTracking()
            .Include(x => x.Order)
                .ThenInclude(x => x!.DistributionCentre)
            .Include(x => x.Order)
                .ThenInclude(x => x!.Items)
            .Where(x => x.DeliveryDate >= start && x.DeliveryDate < end)
            .ToListAsync(cancellationToken);

        return schedules
            .GroupBy(x => new
            {
                DistributionCentreId = x.Order?.DistributionCentreId ?? 0,
                DistributionCentreName = x.Order?.DistributionCentre?.Name ?? "Unknown"
            })
            .Select(g => new SupplierSummaryGroupDto
            {
                DistributionCentre = g.Key.DistributionCentreName,
                TotalPallets = g.Sum(x => x.Order?.TotalPallets ?? x.Order?.Items.Sum(i => i.Pallets) ?? 0),
                Orders = g.Select(x => new SupplierSummaryItemDto
                {
                    OrderNumber = x.Order?.OrderNumber ?? string.Empty,
                    OrderDate = (x.Order?.OrderDate ?? DateTime.MinValue).ToString("yyyy-MM-dd"),
                    DistributionCentre = g.Key.DistributionCentreName,
                    DeliveryDate = x.DeliveryDate.ToString("yyyy-MM-dd"),
                    TotalPallets = x.Order?.TotalPallets ?? x.Order?.Items.Sum(i => i.Pallets) ?? 0
                }).OrderBy(x => x.OrderNumber).ToList()
            })
            .OrderBy(x => x.DistributionCentre)
            .ToList();
    }

    public async Task<List<SupplierSummaryItemDto>> GetSupplierDeliveryAsync(DateTime? date, CancellationToken cancellationToken = default)
    {
        if (date.HasValue)
        {
            Console.WriteLine($"Generating report for date {date.Value:yyyy-MM-dd}");
        }

        var query = _dbContext.DeliverySchedules
            .AsNoTracking()
            .Include(x => x.Order)
                .ThenInclude(x => x!.DistributionCentre)
            .Include(x => x.Order)
                .ThenInclude(x => x!.Items)
            .AsQueryable();

        if (date.HasValue)
        {
            var dbDate = DateTime.SpecifyKind(date.Value.Date, DateTimeKind.Unspecified);
            var start = dbDate.Date;
            var end = start.AddDays(1);
            query = query.Where(x => x.DeliveryDate >= start && x.DeliveryDate < end);
        }

        var schedules = await query.ToListAsync(cancellationToken);

        return schedules
            .Select(x => new SupplierSummaryItemDto
            {
                OrderNumber = x.Order?.OrderNumber ?? string.Empty,
                OrderDate = (x.Order?.OrderDate ?? DateTime.MinValue).ToString("yyyy-MM-dd"),
                DistributionCentre = x.Order?.DistributionCentre?.Name ?? string.Empty,
                DeliveryDate = x.DeliveryDate.ToString("yyyy-MM-dd"),
                TotalPallets = x.Order?.TotalPallets ?? x.Order?.Items.Sum(i => i.Pallets) ?? 0
            })
            .OrderBy(x => x.DeliveryDate)
            .ThenBy(x => x.DistributionCentre)
            .ThenBy(x => x.OrderNumber)
            .ToList();
    }

    public async Task<List<DailyDeliveryGroupDto>> GetDailyDeliveryReportAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"Generating report for date {date:yyyy-MM-dd}");
        var dbDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        Console.WriteLine($"[REPORT QUERY] Using ScheduledDate: {dbDate:yyyy-MM-dd}");
        var start = dbDate.Date;
        var end = start.AddDays(1);

        var schedules = await _dbContext.DeliverySchedules
            .AsNoTracking()
            .Include(x => x.Order)
                .ThenInclude(x => x!.DistributionCentre)
            .Include(x => x.Order)
                .ThenInclude(x => x!.Items)
                    .ThenInclude(x => x.Product)
            .Where(x => x.DeliveryDate >= start && x.DeliveryDate < end)
            .ToListAsync(cancellationToken);

        return schedules
            .GroupBy(x => new
            {
                DistributionCentreId = x.Order?.DistributionCentreId ?? 0,
                DistributionCentreName = x.Order?.DistributionCentre?.Name ?? "Unknown"
            })
            .Select(g => new DailyDeliveryGroupDto
            {
                DistributionCentre = g.Key.DistributionCentreName,
                Deliveries = g.Select(x => new DailyDeliveryOrderDto
                {
                    OrderNumber = x.Order?.OrderNumber ?? string.Empty,
                    ProductSummary = x.Order?.Items
                        .Select(i => $"{i.Product?.SKUCode ?? i.ProductId.ToString()} x {i.Quantity:0.##}")
                        .ToList() ?? new List<string>(),
                    TotalPallets = x.Order?.TotalPallets ?? x.Order?.Items.Sum(i => i.Pallets) ?? 0
                }).OrderBy(x => x.OrderNumber).ToList()
            })
            .OrderBy(x => x.DistributionCentre)
            .ToList();
    }

    public async Task<OrdersReportDto> GetOrdersReportAsync(CancellationToken cancellationToken = default)
    {
        var orders = await _dbContext.Orders
            .AsNoTracking()
            .Include(x => x.DistributionCentre)
            .ToListAsync(cancellationToken);

        return new OrdersReportDto
        {
            TotalOrders = orders.Count(),
            TotalValue = orders.Sum(x => x.TotalValue),
            ByStatus = orders
                .GroupBy(x => x.Status)
                .Select(g => new OrdersByStatusDto
                {
                    Status = g.Key.ToString(),
                    Count = g.Count(),
                    TotalValue = g.Sum(x => x.TotalValue)
                })
                .OrderBy(x => x.Status)
                .ToList(),
            ByDistributionCentre = orders
                .GroupBy(x => new
                {
                    DistributionCentreId = x.DistributionCentreId,
                    DistributionCentreName = x.DistributionCentre?.Name ?? "Unknown"
                })
                .Select(g => new OrdersByDcDto
                {
                    DistributionCentre = g.Key.DistributionCentreName,
                    Count = g.Count(),
                    TotalValue = g.Sum(x => x.TotalValue)
                })
                .OrderBy(x => x.DistributionCentre)
                .ToList()
        };
    }

    public async Task<SalesReportDto> GetSalesSummaryAsync(CancellationToken cancellationToken = default)
    {
        var items = await _dbContext.OrderItems
            .AsNoTracking()
            .Include(x => x.Order)
                .ThenInclude(x => x!.DistributionCentre)
            .Include(x => x.Product)
            .ToListAsync(cancellationToken);

        return new SalesReportDto
        {
            TotalRevenue = items.Sum(x => x.Quantity * x.Price),
            ByProduct = items
                .GroupBy(x => new
                {
                    ProductName = x.ProductName ?? x.Product?.Name ?? string.Empty,
                    SKUCode = x.ProductCode ?? x.Product?.SKUCode ?? string.Empty
                })
                .Select(g => new SalesByProductDto
                {
                    ProductName = g.Key.ProductName,
                    SKUCode = g.Key.SKUCode,
                    TotalQuantity = g.Sum(x => x.Quantity),
                    TotalRevenue = g.Sum(x => x.Quantity * x.Price),
                    TotalPallets = g.Sum(x => x.Pallets)
                })
                .OrderByDescending(x => x.TotalRevenue)
                .ToList(),
            ByDistributionCentre = items
                .Where(x => x.Order is not null)
                .GroupBy(x => new
                {
                    DistributionCentreId = x.Order!.DistributionCentreId,
                    DistributionCentreName = x.Order!.DistributionCentre?.Name ?? "Unknown"
                })
                .Select(g => new SalesByDcDto
                {
                    DistributionCentre = g.Key.DistributionCentreName,
                    TotalOrders = g.Select(x => x.Order!.Id).Distinct().Count(),
                    TotalRevenue = g.Sum(x => x.Quantity * x.Price)
                })
                .OrderByDescending(x => x.TotalRevenue)
                .ToList()
        };
    }

    public async Task<DashboardDto> GetDashboardAsync(DashboardFilterDto filter, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Orders
            .AsNoTracking()
            .Include(order => order.DistributionCentre)
            .Include(order => order.Items)
                .ThenInclude(item => item.Product)
            .Include(order => order.DeliverySchedules)
            .AsQueryable();

        if (filter.FromDate.HasValue)
        {
            var from = DateTime.SpecifyKind(filter.FromDate.Value.Date, DateTimeKind.Unspecified);
            query = query.Where(order => order.DeliveryDate.HasValue && order.DeliveryDate.Value >= from);
        }

        if (filter.ToDate.HasValue)
        {
            var toExclusive = DateTime.SpecifyKind(filter.ToDate.Value.Date.AddDays(1), DateTimeKind.Unspecified);
            query = query.Where(order => order.DeliveryDate.HasValue && order.DeliveryDate.Value < toExclusive);
        }

        if (!string.IsNullOrWhiteSpace(filter.OrderNumber))
        {
            var orderNumber = filter.OrderNumber.Trim();
            query = query.Where(order => order.OrderNumber.Contains(orderNumber));
        }

        if (!string.IsNullOrWhiteSpace(filter.ProductCode))
        {
            var productCode = filter.ProductCode.Trim();
            query = query.Where(order => order.Items.Any(item =>
                (item.ProductCode != null && item.ProductCode.Contains(productCode))
                || (item.Product != null && item.Product.SKUCode.Contains(productCode))));
        }

        if (!string.IsNullOrWhiteSpace(filter.ProductName))
        {
            var productName = filter.ProductName.Trim();
            query = query.Where(order => order.Items.Any(item =>
                (item.ProductName != null && item.ProductName.Contains(productName))
                || (item.Product != null && item.Product.Name.Contains(productName))));
        }

        if (filter.DistributionCentreIds.Count > 0)
        {
            var centreIds = filter.DistributionCentreIds.Distinct().ToArray();
            query = query.Where(order => centreIds.Contains(order.DistributionCentreId));
        }

        if (TryParseOrderStatus(filter.OrderStatus, out var orderStatus))
        {
            query = query.Where(order => order.Status == orderStatus);
        }

        var assignment = filter.ProductionAssignment?.Trim().ToLowerInvariant();
        if (assignment == "assigned")
        {
            query = query.Where(order => order.IsAssignedToProduction);
        }
        else if (assignment is "not assigned" or "unassigned")
        {
            query = query.Where(order => !order.IsAssignedToProduction);
        }

        var deliveryStatus = filter.DeliveryStatus?.Trim().ToLowerInvariant();
        if (deliveryStatus == "scheduled")
        {
            query = query.Where(order => order.DeliverySchedules.Any());
        }
        else if (deliveryStatus == "unscheduled")
        {
            query = query.Where(order => !order.DeliverySchedules.Any());
        }

        var orders = await query
            .OrderByDescending(order => order.OrderDate)
            .ThenBy(order => order.OrderNumber)
            .ToListAsync(cancellationToken);

        var today = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Unspecified);
        var dashboardOrders = orders.Select(order =>
        {
            var isScheduled = order.DeliverySchedules.Count > 0;
            var isOverdue = order.DeliveryDate.HasValue && order.DeliveryDate.Value.Date < today && !isScheduled;
            var hasPriceMissing = order.Items.Any(item => item.IsPriceMissing);
            var hasPricingIssue = order.Items.Any(item => item.IsPriceMismatch);

            return new
            {
                Order = order,
                IsScheduled = isScheduled,
                IsOverdue = isOverdue,
                HasPriceMissing = hasPriceMissing,
                HasPricingIssue = hasPricingIssue,
                TotalQuantity = order.Items.Sum(item => item.Quantity),
                TotalPallets = order.TotalPallets > 0 ? order.TotalPallets : order.Items.Sum(item => item.Pallets)
            };
        }).ToList();

        var exception = filter.Exception?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(exception) && exception != "any")
        {
            dashboardOrders = dashboardOrders.Where(row => exception switch
            {
                "not assigned" or "unassigned" => !row.Order.IsAssignedToProduction,
                "flagged" => row.Order.Status == OrderStatus.Flagged,
                "no price configured" => row.HasPriceMissing,
                "pricing issue" => row.HasPricingIssue,
                "overdue" => row.IsOverdue,
                _ => true
            }).ToList();
        }

        var mappedOrders = dashboardOrders.Select(row => new DashboardOrderDto
        {
            OrderNumber = row.Order.OrderNumber,
            DistributionCentre = row.Order.DistributionCentre?.Name ?? "Unknown",
            OrderDate = row.Order.OrderDate.ToString("yyyy-MM-dd"),
            DeliveryDate = row.Order.DeliveryDate?.ToString("yyyy-MM-dd"),
            IsAssignedToProduction = row.Order.IsAssignedToProduction,
            IsScheduled = row.IsScheduled,
            TotalQuantity = row.TotalQuantity,
            TotalPallets = row.TotalPallets,
            TotalValue = row.Order.TotalValue,
            Status = row.Order.Status.ToString()
        }).ToList();

        return new DashboardDto
        {
            TotalOrders = mappedOrders.Count,
            TotalOrderValue = mappedOrders.Sum(order => order.TotalValue),
            AssignedOrders = mappedOrders.Count(order => order.IsAssignedToProduction),
            UnassignedOrders = mappedOrders.Count(order => !order.IsAssignedToProduction),
            ScheduledOrders = mappedOrders.Count(order => order.IsScheduled),
            UnscheduledOrders = mappedOrders.Count(order => !order.IsScheduled),
            FlaggedOrders = mappedOrders.Count(order => order.Status == OrderStatus.Flagged.ToString()),
            OverdueOrders = dashboardOrders.Count(row => row.IsOverdue),
            Orders = mappedOrders,
            DistributionCentres = dashboardOrders
                .GroupBy(row => row.Order.DistributionCentre?.Name ?? "Unknown")
                .Select(group => new DashboardDistributionCentreDto
                {
                    DistributionCentre = group.Key,
                    TotalOrders = group.Count(),
                    Assigned = group.Count(row => row.Order.IsAssignedToProduction),
                    NotAssigned = group.Count(row => !row.Order.IsAssignedToProduction),
                    Scheduled = group.Count(row => row.IsScheduled),
                    Unscheduled = group.Count(row => !row.IsScheduled),
                    TotalValue = group.Sum(row => row.Order.TotalValue)
                })
                .OrderBy(row => row.DistributionCentre)
                .ToList(),
            RequiresAttention = dashboardOrders
                .SelectMany(row => GetDashboardExceptions(row.Order, row.HasPriceMissing, row.HasPricingIssue, row.IsOverdue)
                    .Select(item => new DashboardAttentionDto
                    {
                        OrderNumber = row.Order.OrderNumber,
                        DistributionCentre = row.Order.DistributionCentre?.Name ?? "Unknown",
                        Exception = item,
                        TotalValue = row.Order.TotalValue
                    }))
                .OrderBy(row => row.OrderNumber)
                .ToList(),
            Products = dashboardOrders
                .SelectMany(row => row.Order.Items.Select(item => new
                {
                    ProductName = item.ProductName ?? item.Product?.Name ?? string.Empty,
                    SKUCode = item.ProductCode ?? item.Product?.SKUCode ?? string.Empty,
                    item.Quantity,
                    Revenue = item.Quantity * item.Price,
                    item.Pallets
                }))
                .GroupBy(item => new { item.ProductName, item.SKUCode })
                .Select(group => new DashboardProductDto
                {
                    ProductName = group.Key.ProductName,
                    SKUCode = group.Key.SKUCode,
                    TotalQuantity = group.Sum(item => item.Quantity),
                    TotalRevenue = group.Sum(item => item.Revenue),
                    TotalPallets = group.Sum(item => item.Pallets)
                })
                .OrderByDescending(item => item.TotalRevenue)
                .ToList()
        };
    }

    private static bool TryParseOrderStatus(string? value, out OrderStatus status)
    {
        status = default;
        if (string.IsNullOrWhiteSpace(value) || value.Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Enum.TryParse(value, true, out status))
        {
            return true;
        }

        if (int.TryParse(value, out var numeric) && Enum.IsDefined(typeof(OrderStatus), numeric))
        {
            status = (OrderStatus)numeric;
            return true;
        }

        return false;
    }

    private static List<string> GetDashboardExceptions(Order order, bool hasPriceMissing, bool hasPricingIssue, bool isOverdue)
    {
        var exceptions = new List<string>();
        if (!order.IsAssignedToProduction) exceptions.Add("Not Assigned");
        if (order.Status == OrderStatus.Flagged) exceptions.Add("Flagged");
        if (hasPriceMissing) exceptions.Add("No Price Configured");
        if (hasPricingIssue) exceptions.Add("Pricing Issue");
        if (isOverdue) exceptions.Add("Overdue");
        return exceptions;
    }

    private static string ResolveSupplier(Order order)
    {
        return string.Join(", ", order.Items.Select(item =>
            item.Metadata.FirstOrDefault(entry => entry.Key.Equals("Supplier", StringComparison.OrdinalIgnoreCase)
                || entry.Key.Equals("SupplierName", StringComparison.OrdinalIgnoreCase)
                || entry.Key.Equals("Vendor", StringComparison.OrdinalIgnoreCase)).Value)
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().OrderBy(value => value));
    }
}
