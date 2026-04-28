using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;

namespace OrderProcessingApp.Services;

public class ProductionService : IProductionService
{
    private readonly AppDbContext _dbContext;

    public ProductionService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ProductionResponseDto> GetProductionByDateAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        var selectedDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        var nextDate = selectedDate.AddDays(1);
        Console.WriteLine($"[DATE FLOW] Service incoming date: {date:O}, Kind={date.Kind}");
        Console.WriteLine($"[DATE FLOW] Normalized date:       {selectedDate:O}, Kind={selectedDate.Kind}");
        Console.WriteLine($"[PRODUCTION] Fetching for {selectedDate:yyyy-MM-dd}");
        Console.WriteLine("[PROD] Including VALIDATED orders in production");

        // Fetch full schedule records so we can log each match
        var scheduleMatches = await _dbContext.DeliverySchedules
            .AsNoTracking()
            .Where(x => x.DeliveryDate >= selectedDate && x.DeliveryDate < nextDate)
            .ToListAsync(cancellationToken);

        foreach (var s in scheduleMatches)
            Console.WriteLine($"[SCHEDULE MATCH] OrderId={s.OrderId}, Date={s.DeliveryDate:yyyy-MM-dd}, Kind={s.DeliveryDate.Kind}");

        var scheduledOrderIds = await _dbContext.DeliverySchedules
            .AsNoTracking()
            .Where(ds => ds.DeliveryDate >= selectedDate && ds.DeliveryDate < nextDate)
            .Select(ds => ds.OrderId)
            .ToListAsync(cancellationToken);

        Console.WriteLine($"[SCHEDULE] scheduledOrderIds for {selectedDate:yyyy-MM-dd}: [{string.Join(", ", scheduledOrderIds)}] (count={scheduledOrderIds.Count})");

        // --- Scheduled ---
        var scheduledOrders = await _dbContext.Orders
            .AsNoTracking()
            .Where(x => scheduledOrderIds.Contains(x.Id)
                && (x.Status == OrderStatus.Approved
                    || x.Status == OrderStatus.Processed
                    || x.Status == OrderStatus.Validated))
            .Include(x => x.DistributionCentre)
            .Include(x => x.Items)
            .ToListAsync(cancellationToken);

        Console.WriteLine($"[PRODUCTION] Filtered orders: {scheduledOrders.Count}");
        foreach (var o in scheduledOrders)
            Console.WriteLine($"[SCHEDULED ORDER] Id={o.Id}, Status={o.Status}, DeliveryDate={o.DeliveryDate:yyyy-MM-dd}, Kind={o.DeliveryDate.Kind}");

        var scheduledItems = scheduledOrders
            .SelectMany(order => order.Items.Select(item => new
            {
                item.ProductId,
                ProductCode = item.ProductCode ?? string.Empty,
                ProductName = item.ProductName ?? string.Empty,
                DistributionCentre = order.DistributionCentre?.Name ?? string.Empty,
                Status = order.Status,
                item.Quantity,
                item.Pallets
            }))
            .ToList();

        Console.WriteLine($"[PRODUCTION] Items count: {scheduledItems.Count}");

        var scheduled = scheduledItems
            .GroupBy(x => new { x.ProductId, x.ProductCode, x.ProductName, x.DistributionCentre })
            .Select(g => new ProductionDto
            {
                ProductId = g.Key.ProductId,
                ProductCode = g.Key.ProductCode,
                ProductName = g.Key.ProductName,
                DistributionCentre = g.Key.DistributionCentre,
                Status = GetHighestProductionStatus(g.Select(x => x.Status)).ToString(),
                TotalQuantity = g.Sum(x => x.Quantity),
                TotalPallets = Math.Round(g.Sum(x => x.Pallets), 2),
                OpeningStock = 0,
                ProductionRequired = g.Sum(x => x.Quantity)
            })
            .OrderBy(x => x.DistributionCentre)
            .ThenBy(x => x.ProductName)
            .ToList();

        Console.WriteLine($"[FINAL] Scheduled groups: {scheduled.Count}");

        // --- Unscheduled ---
        var unscheduledOrders = await _dbContext.Orders
            .AsNoTracking()
            .Where(x => x.DeliveryDate >= selectedDate
                && x.DeliveryDate < nextDate
                && !_dbContext.DeliverySchedules.Any(ds =>
                    ds.OrderId == x.Id &&
                    ds.DeliveryDate >= selectedDate && ds.DeliveryDate < nextDate)
            )
            .Where(x => x.Status == OrderStatus.Approved
                || x.Status == OrderStatus.Processed
                || x.Status == OrderStatus.Validated)
            .Include(x => x.DistributionCentre)
            .Include(x => x.Items)
            .ToListAsync(cancellationToken);

        foreach (var o in unscheduledOrders)
        {
            Console.WriteLine($"[UNSCHEDULED ORDER] Id={o.Id}, Status={o.Status}, DeliveryDate={o.DeliveryDate:yyyy-MM-dd}, Kind={o.DeliveryDate.Kind}");
            Console.WriteLine($"[UNSCHEDULED INCLUDE] OrderId={o.Id}, DeliveryDate={o.DeliveryDate:yyyy-MM-dd}");
        }

        var unscheduledItems = unscheduledOrders
            .SelectMany(order => order.Items.Select(item => new
            {
                item.ProductId,
                ProductCode = item.ProductCode ?? string.Empty,
                ProductName = item.ProductName ?? string.Empty,
                DistributionCentre = order.DistributionCentre?.Name ?? string.Empty,
                Status = order.Status,
                item.Quantity,
                item.Pallets
            }))
            .ToList();

        var unscheduled = unscheduledItems
            .GroupBy(x => new { x.ProductId, x.ProductCode, x.ProductName, x.DistributionCentre })
            .Select(g => new ProductionDto
            {
                ProductId = g.Key.ProductId,
                ProductCode = g.Key.ProductCode,
                ProductName = g.Key.ProductName,
                DistributionCentre = g.Key.DistributionCentre,
                Status = GetHighestProductionStatus(g.Select(x => x.Status)).ToString(),
                TotalQuantity = g.Sum(x => x.Quantity),
                TotalPallets = Math.Round(g.Sum(x => x.Pallets), 2),
                OpeningStock = 0,
                ProductionRequired = g.Sum(x => x.Quantity)
            })
            .OrderBy(x => x.DistributionCentre)
            .ThenBy(x => x.ProductName)
            .ToList();

        Console.WriteLine($"[FINAL] Unscheduled groups: {unscheduled.Count}");

        return new ProductionResponseDto
        {
            Scheduled = scheduled,
            Unscheduled = unscheduled
        };
    }

    private static OrderStatus GetHighestProductionStatus(IEnumerable<OrderStatus> statuses)
    {
        if (statuses.Any(x => x == OrderStatus.Processed))
        {
            return OrderStatus.Processed;
        }

        if (statuses.Any(x => x == OrderStatus.Approved))
        {
            return OrderStatus.Approved;
        }

        return OrderStatus.Validated;
    }

    public async Task<List<ProductionPlanDto>> CreateAsync(List<int> orderIds, CancellationToken cancellationToken = default)
    {
        var orders = await _dbContext.Orders
            .AsNoTracking()
            .Include(o => o.Items)
                .ThenInclude(i => i.Product)
            .Where(o => orderIds.Contains(o.Id))
            .ToListAsync(cancellationToken);

        var notFound = orderIds.Except(orders.Select(o => o.Id)).ToList();
        if (notFound.Count > 0)
            throw new KeyNotFoundException($"Orders not found: {string.Join(", ", notFound)}.");

        var productionReadyOrders = orders
            .Where(o => o.Status == OrderStatus.Approved || o.Status == OrderStatus.Processed)
            .ToList();

        if (productionReadyOrders.Count == 0)
        {
            return new List<ProductionPlanDto>();
        }

        var productionLines = productionReadyOrders
            .SelectMany(o => o.Items.Select(i => new
            {
                i.ProductId,
                ProductName = i.ProductName ?? i.Product?.Name ?? string.Empty,
                Quantity = i.Quantity,
                Pallets = i.Pallets,
                o.DistributionCentreId,
                PlanDate = ToDbDate(o.DeliveryDate)
            }))
            .ToList();

        var groups = productionLines
            .GroupBy(x => new { x.ProductId, x.PlanDate })
            .ToList();

        if (groups.Count == 0)
        {
            return new List<ProductionPlanDto>();
        }

        foreach (var group in groups)
        {
            var productionQty = group.Sum(x => x.Quantity);

            var existing = await _dbContext.ProductionPlans
                .FirstOrDefaultAsync(x => x.ProductId == group.Key.ProductId && x.Date == group.Key.PlanDate, cancellationToken);

            if (existing is null)
            {
                existing = new ProductionPlan
                {
                    ProductId = group.Key.ProductId,
                    Date = group.Key.PlanDate,
                    OpeningStock = 0,
                    ProductionQuantity = productionQty,
                    ClosingStock = productionQty
                };
                _dbContext.ProductionPlans.Add(existing);
            }
            else
            {
                existing.ProductionQuantity = productionQty;
                existing.ClosingStock = existing.OpeningStock + productionQty
                    - await CalculateTotalDemandAsync(existing.ProductId, existing.Date, cancellationToken);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return await GetPlansByDateAsync(groups.Select(g => g.Key.PlanDate).Distinct().First(), cancellationToken);
    }

    public async Task CreateOrUpdatePlanAsync(ProductionPlanUpsertDto dto, CancellationToken cancellationToken = default)
    {
        var planDate = ToDbDate(dto.Date);

        var productExists = await _dbContext.Products
            .AsNoTracking()
            .AnyAsync(x => x.Id == dto.ProductId, cancellationToken);

        if (!productExists)
        {
            throw new KeyNotFoundException("Product not found.");
        }

        var totalDemand = await CalculateTotalDemandAsync(dto.ProductId, planDate, cancellationToken);
        var closingStock = dto.OpeningStock + dto.ProductionQuantity - totalDemand;

        var existing = await _dbContext.ProductionPlans
            .FirstOrDefaultAsync(x => x.ProductId == dto.ProductId && x.Date == planDate, cancellationToken);

        if (existing is null)
        {
            existing = new ProductionPlan
            {
                ProductId = dto.ProductId,
                Date = planDate,
                OpeningStock = dto.OpeningStock,
                ProductionQuantity = dto.ProductionQuantity,
                ClosingStock = closingStock,
                Notes = dto.Notes
            };

            _dbContext.ProductionPlans.Add(existing);
        }
        else
        {
            existing.OpeningStock = dto.OpeningStock;
            existing.ProductionQuantity = dto.ProductionQuantity;
            existing.ClosingStock = closingStock;
            existing.Notes = dto.Notes;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<ProductionPlanDto>> GetPlansByDateAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        var planDate = ToDbDate(date);

        var plans = await _dbContext.ProductionPlans
            .AsNoTracking()
            .Include(x => x.Product)
            .Where(x => x.Date == planDate)
            .OrderBy(x => x.Product!.Name)
            .ToListAsync(cancellationToken);

        var output = new List<ProductionPlanDto>();
        foreach (var plan in plans)
        {
            var totalDemand = await CalculateTotalDemandAsync(plan.ProductId, planDate, cancellationToken);
            var closingStock = plan.OpeningStock + plan.ProductionQuantity - totalDemand;

            output.Add(new ProductionPlanDto
            {
                Id = plan.Id,
                ProductId = plan.ProductId,
                ProductName = plan.Product?.Name ?? string.Empty,
                Date = plan.Date.ToString("yyyy-MM-dd"),
                OpeningStock = plan.OpeningStock,
                ProductionQuantity = plan.ProductionQuantity,
                TotalOrderDemand = totalDemand,
                ClosingStock = closingStock,
                HasInsufficientStock = closingStock < 0,
                Notes = plan.Notes
            });
        }

        return output;
    }

    public async Task<List<StockCheckDto>> CheckStockAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        var planDate = ToDbDate(date);

        var demandByProduct = await _dbContext.OrderItems
            .AsNoTracking()
            .Where(x => x.Order != null
                && x.Order.DeliveryDate.Date == planDate.Date
                && (x.Order.Status == OrderStatus.Approved || x.Order.Status == OrderStatus.Processed))
            .GroupBy(x => new { x.ProductId, ProductName = x.Product!.Name })
            .Select(g => new
            {
                g.Key.ProductId,
                g.Key.ProductName,
                RequiredQuantity = g.Sum(x => x.Quantity)
            })
            .ToListAsync(cancellationToken);

        var plans = await _dbContext.ProductionPlans
            .AsNoTracking()
            .Where(x => x.Date == planDate)
            .ToListAsync(cancellationToken);

        var result = new List<StockCheckDto>();
        foreach (var demand in demandByProduct)
        {
            var plan = plans.FirstOrDefault(x => x.ProductId == demand.ProductId);
            var available = plan is null ? 0 : plan.OpeningStock + plan.ProductionQuantity;
            var shortfall = demand.RequiredQuantity > available ? demand.RequiredQuantity - available : 0;

            result.Add(new StockCheckDto
            {
                ProductId = demand.ProductId,
                ProductName = demand.ProductName,
                Date = planDate.ToString("yyyy-MM-dd"),
                RequiredQuantity = demand.RequiredQuantity,
                AvailableQuantity = available,
                Shortfall = shortfall,
                IsSufficient = shortfall <= 0
            });
        }

        return result.OrderBy(x => x.ProductName).ToList();
    }

    private async Task<decimal> CalculateTotalDemandAsync(int productId, DateTime date, CancellationToken cancellationToken)
    {
        return await _dbContext.OrderItems
            .AsNoTracking()
            .Where(x => x.ProductId == productId
                && x.Order != null
                && x.Order.DeliveryDate.Date == date.Date
                && (x.Order.Status == OrderStatus.Approved || x.Order.Status == OrderStatus.Processed))
            .SumAsync(x => (decimal?)x.Quantity, cancellationToken) ?? 0;
    }

    private static DateTime ToDbDate(DateTime input)
    {
        return DateTime.SpecifyKind(input.Date, DateTimeKind.Unspecified);
    }
}
