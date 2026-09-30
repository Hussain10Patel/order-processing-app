using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;

namespace OrderProcessingApp.Services;

public sealed class ProductionAssignmentService : IProductionAssignmentService
{
    private readonly AppDbContext _dbContext;

    public ProductionAssignmentService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
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
            .Where(order => order.Status == OrderStatus.Approved);

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

        if (order.Status != OrderStatus.Approved)
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

        if (order.DeliveryDate != normalizedDate || !order.IsAssignedToProduction)
        {
            order.DeliveryDate = normalizedDate;
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

        if (order.Status != OrderStatus.Approved)
        {
            throw new InvalidOperationException("Only approved orders can be unassigned from production.");
        }

        if (order.IsAssignedToProduction)
        {
            order.IsAssignedToProduction = false;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

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
            Status = order.Status.ToString(),
            IsScheduled = order.DeliverySchedules.Count > 0,
            IsAssignedToProduction = order.IsAssignedToProduction
        };
    }
}