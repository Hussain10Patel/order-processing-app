using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;

namespace OrderProcessingApp.Services;

public sealed class ProductionAssignmentService : IProductionAssignmentService
{
    private readonly AppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly DeliveryClock? _clock;

    public ProductionAssignmentService(AppDbContext dbContext, IAuditService? auditService = null, DeliveryClock? clock = null)
    {
        _dbContext = dbContext;
        _auditService = auditService ?? new AuditService(dbContext);
        _clock = clock;
    }

    public async Task<List<ProductionAssignmentOrderDto>> GetApprovedOrdersAsync(
        string? assignment,
        IReadOnlyCollection<int>? distributionCentreIds,
        string? orderNumber,
        DateTime? orderDateFrom,
        DateTime? orderDateTo,
        DateTime? deliveryDateFrom,
        DateTime? deliveryDateTo,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Orders
            .AsNoTracking()
            .Include(order => order.DistributionCentre)
            .Include(order => order.DeliverySchedules)
            .Where(order => order.Status == OrderStatus.Approved || order.Status == OrderStatus.Scheduled
                || (order.IsAssignedToProduction && (order.Status == OrderStatus.EnRoute || order.Status == OrderStatus.Delivered)));

        var normalizedAssignment = assignment?.Trim().ToLowerInvariant();
        if (normalizedAssignment == "assigned")
        {
            query = query.Where(order => order.IsAssignedToProduction);
        }
        else if (normalizedAssignment == "unassigned")
        {
            query = query.Where(order => !order.IsAssignedToProduction);
        }

        if (distributionCentreIds is { Count: > 0 })
        {
            var selectedIds = distributionCentreIds.Distinct().ToArray();
            query = query.Where(order => selectedIds.Contains(order.DistributionCentreId));
        }

        if (!string.IsNullOrWhiteSpace(orderNumber))
        {
            var search = orderNumber.Trim();
            query = query.Where(order => order.OrderNumber.Contains(search));
        }

        if (orderDateFrom.HasValue)
        {
            var start = DateTime.SpecifyKind(orderDateFrom.Value.Date, DateTimeKind.Unspecified);
            query = query.Where(order => order.OrderDate >= start);
        }

        if (orderDateTo.HasValue)
        {
            var endExclusive = DateTime.SpecifyKind(orderDateTo.Value.Date.AddDays(1), DateTimeKind.Unspecified);
            query = query.Where(order => order.OrderDate < endExclusive);
        }

        if (deliveryDateFrom.HasValue)
        {
            var start = DateTime.SpecifyKind(deliveryDateFrom.Value.Date, DateTimeKind.Unspecified);
            query = query.Where(order => order.DeliveryDate.HasValue && order.DeliveryDate.Value >= start);
        }

        if (deliveryDateTo.HasValue)
        {
            var endExclusive = DateTime.SpecifyKind(deliveryDateTo.Value.Date.AddDays(1), DateTimeKind.Unspecified);
            query = query.Where(order => order.DeliveryDate.HasValue && order.DeliveryDate.Value < endExclusive);
        }

        var orders = await query
            .OrderBy(order => order.DeliveryDate)
            .ThenBy(order => order.OrderNumber)
            .ToListAsync(cancellationToken);

        return orders.Select(MapOrder).ToList();
    }

    public async Task<ProductionAssignmentOrderDto?> SetDeliveryDateAsync(
        int orderId,
        DateTime deliveryDate,
        CancellationToken cancellationToken = default)
    {
        // The global Order query filter excludes soft-deleted/inactive orders.
        var order = await _dbContext.Orders
            .Include(entity => entity.DistributionCentre)
            .Include(entity => entity.DeliverySchedules)
            .FirstOrDefaultAsync(entity => entity.Id == orderId, cancellationToken);

        if (order is null)
        {
            return null;
        }

        DeliveryWorkflowMutations.EnsureNotDispatched(order, _clock?.UtcNow);
        if (order.Status is not (OrderStatus.Approved or OrderStatus.Scheduled))
        {
            throw new InvalidOperationException("Only approved orders can be assigned to production.");
        }

        var normalizedDate = DateTime.SpecifyKind(deliveryDate.Date, DateTimeKind.Unspecified);
        if (normalizedDate < order.OrderDate.Date)
        {
            throw new InvalidOperationException("Delivery date cannot be earlier than order date.");
        }

        var scheduledOrder = await _dbContext.DeliverySchedules
            .FirstOrDefaultAsync(schedule => schedule.OrderId == orderId, cancellationToken);
        var originalStatus = order.Status;
        var originalAssignment = order.IsAssignedToProduction;
        var originalDate = order.DeliveryDate;

        if (scheduledOrder is null)
        {
            scheduledOrder = new DeliverySchedule { OrderId = order.Id, DeliveryDate = normalizedDate, Status = "Scheduled" };
            _dbContext.DeliverySchedules.Add(scheduledOrder);
        }
        scheduledOrder.DeliveryDate = normalizedDate;
        scheduledOrder.Status = OrderStatus.Scheduled.ToString();
        if (!order.DeliverySchedules.Contains(scheduledOrder))
        {
            order.DeliverySchedules.Add(scheduledOrder);
        }

        if (order.DeliveryDate != normalizedDate || !order.IsAssignedToProduction)
        {
            DeliveryWorkflowMutations.UpdateScheduledDate(order, normalizedDate, _clock?.BusinessTimeZone);
            order.IsAssignedToProduction = true;

            if (scheduledOrder is not null)
            {
                scheduledOrder.DeliveryDate = normalizedDate;
            }

            var plannerOrderEvents = await _dbContext.ProductionDeliveryPlanEvents
                .Where(plannerEvent => plannerEvent.EventType == ProductionDeliveryPlanEventType.Order
                    && plannerEvent.OrderId == orderId)
                .ToListAsync(cancellationToken);

            var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
            foreach (var plannerEvent in plannerOrderEvents)
            {
                plannerEvent.PlannedDeliveryDate = normalizedDate;
                plannerEvent.UpdatedAt = now;
            }
        }

        order.Status = OrderStatus.Scheduled;
        _dbContext.Entry(order).Property(x => x.Status).IsModified = true;
        if (originalStatus != order.Status) _auditService.TrackChange("Order", order.Id, "Status", originalStatus.ToString(), order.Status.ToString());
        if (!originalAssignment) _auditService.TrackChange("Order", order.Id, "ProductionAssignment", "Unassigned", "Assigned");
        if (originalDate != normalizedDate) _auditService.TrackChange("Order", order.Id, "DeliveryDate", originalDate?.ToString("yyyy-MM-dd"), normalizedDate.ToString("yyyy-MM-dd"));
        await _dbContext.SaveChangesAsync(cancellationToken);

        return MapOrder(order);
    }

    public async Task<ProductionAssignmentOrderDto?> UnassignAsync(
        int orderId,
        CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.Orders
            .Include(entity => entity.DistributionCentre)
            .Include(entity => entity.DeliverySchedules)
            .FirstOrDefaultAsync(entity => entity.Id == orderId, cancellationToken);

        if (order is null)
        {
            return null;
        }

        DeliveryWorkflowMutations.EnsureNotDispatched(order, _clock?.UtcNow);
        if (order.Status is not (OrderStatus.Approved or OrderStatus.Scheduled))
        {
            throw new InvalidOperationException("Only approved orders can be unassigned from production.");
        }

        var originalStatus = order.Status;
        var originalAssignment = order.IsAssignedToProduction;
        DeliveryWorkflowMutations.ClearAssignmentAndSchedule(_dbContext, order, _clock?.UtcNow);
        if (originalStatus != order.Status) _auditService.TrackChange("Order", order.Id, "Status", originalStatus.ToString(), order.Status.ToString());
        if (originalAssignment) _auditService.TrackChange("Order", order.Id, "ProductionAssignment", "Assigned", "Unassigned");
        await _dbContext.SaveChangesAsync(cancellationToken);

        return MapOrder(order);
    }

    private static ProductionAssignmentOrderDto MapOrder(Order order)
    {
        return new ProductionAssignmentOrderDto
        {
            Id = order.Id,
            OrderNumber = order.OrderNumber,
            DistributionCentreId = order.DistributionCentreId,
            DistributionCentreName = order.DistributionCentre?.Name ?? string.Empty,
            OrderDate = order.OrderDate.ToString("yyyy-MM-dd"),
            DeliveryDate = order.DeliveryDate?.ToString("yyyy-MM-dd"),
            OriginalCsvDeliveryDate = order.OriginalCsvDeliveryDate?.ToString("yyyy-MM-dd"),
            EnRouteAtUtc = order.EnRouteAtUtc,
            Status = order.Status.ToString(),
            IsScheduled = order.DeliverySchedules.Count > 0,
            IsAssignedToProduction = order.IsAssignedToProduction
        };
    }
}