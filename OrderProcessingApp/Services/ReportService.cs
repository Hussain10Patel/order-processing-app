using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;

namespace OrderProcessingApp.Services;

public class ReportService : IReportService
{
    private readonly AppDbContext _dbContext;

    public ReportService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ReportSummaryDto> GetSummaryByDeliveryDateAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        var selectedDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        var start = selectedDate.Date;
        var end = start.AddDays(1);

        var orders = await _dbContext.Orders
            .AsNoTracking()
            .Include(x => x.DistributionCentre)
            .Include(x => x.Items)
                .ThenInclude(x => x.Product)
            .Where(x => x.DeliveryDate >= start && x.DeliveryDate < end)
            .ToListAsync(cancellationToken);

        var orderIds = orders.Select(x => x.Id).ToList();
        var schedulesByOrder = await _dbContext.DeliverySchedules
            .AsNoTracking()
            .Where(x => orderIds.Contains(x.OrderId) && x.DeliveryDate >= start && x.DeliveryDate < end)
            .GroupBy(x => x.OrderId)
            .ToDictionaryAsync(x => x.Key, x => x.Select(ds => ds.Status).ToList(), cancellationToken);

        var planQtyByProduct = await _dbContext.ProductionPlans
            .AsNoTracking()
            .Where(x => x.Date >= start && x.Date < end)
            .GroupBy(x => x.ProductId)
            .Select(g => new
            {
                ProductId = g.Key,
                Quantity = g.Sum(x => x.ProductionQuantity)
            })
            .ToDictionaryAsync(x => x.ProductId, x => x.Quantity, cancellationToken);

        var orderComputedStatuses = orders.Select(order => new
        {
            Order = order,
            Status = ComputeReportStatus(order, schedulesByOrder, planQtyByProduct),
            TotalValue = order.Items.Sum(i => i.Quantity * i.Price)
        }).ToList();

        var allItems = orders.SelectMany(x => x.Items).ToList();

        return new ReportSummaryDto
        {
            TotalOrders = orders.Count,
            TotalValue = allItems.Sum(i => i.Quantity * i.Price),
            OrdersByStatus = orderComputedStatuses
                .GroupBy(x => x.Status)
                .Select(g => new ReportStatusCountDto
                {
                    Status = g.Key,
                    Count = g.Count(),
                    TotalValue = g.Sum(x => x.TotalValue)
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
                    PoNumber = x.Order.OrderNumber,
                    Dc = x.Order.DistributionCentre?.Name ?? string.Empty,
                    DeliveryDate = x.Order.DeliveryDate.ToString("yyyy-MM-dd"),
                    Status = x.Status
                })
                .OrderBy(x => x.Dc)
                .ThenBy(x => x.PoNumber)
                .ToList()
        };
    }

    public async Task<List<SupplierSummaryGroupDto>> GetSupplierSummaryAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"Generating report for date {date:yyyy-MM-dd}");
        var dbDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
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

    private static string ComputeReportStatus(
        Order order,
        IReadOnlyDictionary<int, List<string>> schedulesByOrder,
        IReadOnlyDictionary<int, decimal> planQtyByProduct)
    {
        var hasDeliverySchedule = schedulesByOrder.ContainsKey(order.Id);
        if (hasDeliverySchedule)
        {
            return "Delivered";
        }

        var requiredByProduct = order.Items
            .GroupBy(i => i.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));

        var hasProduction = requiredByProduct.Any(kvp =>
            planQtyByProduct.TryGetValue(kvp.Key, out var plannedQty) && plannedQty > 0);

        var isProductionComplete = requiredByProduct.Count > 0 && requiredByProduct.All(kvp =>
            planQtyByProduct.TryGetValue(kvp.Key, out var plannedQty) && plannedQty >= kvp.Value);

        if (order.Status == OrderStatus.Processed || isProductionComplete)
        {
            return "Ready";
        }

        if (order.Status == OrderStatus.Approved || hasProduction)
        {
            return "InProduction";
        }

        return "Pending";
    }
}
