using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;

namespace OrderProcessingApp.Services;

public sealed class DeliveryLifecycleService
{
    private readonly AppDbContext _dbContext;
    private readonly DeliveryClock _clock;
    private readonly ILogger<DeliveryLifecycleService> _logger;

    public DeliveryLifecycleService(AppDbContext dbContext, DeliveryClock clock, ILogger<DeliveryLifecycleService> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _logger = logger;
    }

    public async Task<DeliveryLifecycleDto> SetEnRouteAsync(int orderId, decimal? durationHours, CancellationToken cancellationToken = default)
    {
        DeliveryClock.ExpectedDeliveryTime(_clock.UtcNow, durationHours);
        var order = await _dbContext.Orders.Include(x => x.DeliverySchedules)
            .FirstOrDefaultAsync(x => x.Id == orderId, cancellationToken)
            ?? throw new KeyNotFoundException($"Order not found. OrderId={orderId}.");

        // A repeated dispatch request must not restart a saved timer.
        if (order.Status == OrderStatus.EnRoute)
        {
            if (order.ExpectedDeliveryDurationHours != durationHours)
            {
                throw new InvalidOperationException("This order is already En Route. Its saved delivery timer cannot be changed.");
            }
            return Map(order);
        }

        if (order.Status != OrderStatus.Scheduled || !order.IsAssignedToProduction
            || !order.DeliveryDate.HasValue || order.DeliverySchedules.Count == 0)
        {
            throw new InvalidOperationException("Only assigned, Scheduled orders with a delivery schedule can be set to En Route.");
        }

        var now = _clock.UtcNow;
        var expected = DeliveryClock.ExpectedDeliveryTime(now, durationHours);
        order.Status = OrderStatus.EnRoute;
        order.EnRouteAtUtc = now;
        order.ExpectedDeliveryDurationHours = durationHours;
        order.ExpectedDeliveryAtUtc = expected;
        order.IsDeliveryEstimated = false;
        foreach (var schedule in order.DeliverySchedules)
        {
            schedule.Status = OrderStatus.EnRoute.ToString();
        }
        TrackStatusChange(order.Id, OrderStatus.Scheduled, OrderStatus.EnRoute, now);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Map(order);
    }

    public async Task<int> CompleteDueOrdersAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var today = _clock.BusinessToday(now);
        var candidateIds = await _dbContext.Orders.AsNoTracking()
            .Where(x => x.IsAssignedToProduction && x.DeliverySchedules.Any()
                && (x.Status == OrderStatus.Scheduled || x.Status == OrderStatus.EnRoute))
            .Select(x => x.Id).ToListAsync(cancellationToken);

        var completed = 0;
        foreach (var id in candidateIds)
        {
            var order = await _dbContext.Orders.Include(x => x.DeliverySchedules)
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (order is null || !order.IsAssignedToProduction || order.DeliverySchedules.Count == 0)
            {
                continue;
            }
            if ((order.Status == OrderStatus.Scheduled && (!order.DeliveryDate.HasValue || order.EnRouteAtUtc.HasValue))
                || (order.Status == OrderStatus.EnRoute && (!order.EnRouteAtUtc.HasValue || !order.ExpectedDeliveryAtUtc.HasValue)))
            {
                _logger.LogWarning("Delivery transition skipped for order {OrderId}: persisted delivery date/timing is inconsistent with status {Status}.", id, order.Status);
                continue;
            }

            var isDue = order.Status == OrderStatus.Scheduled
                ? order.EnRouteAtUtc is null && order.DeliveryDate.HasValue && order.DeliveryDate.Value.Date < today
                : order.Status == OrderStatus.EnRoute && order.EnRouteAtUtc.HasValue
                    && order.ExpectedDeliveryAtUtc.HasValue && order.ExpectedDeliveryAtUtc.Value <= now;
            if (!isDue)
            {
                continue;
            }

            var oldStatus = order.Status;
            order.Status = OrderStatus.Delivered;
            order.DeliveredAtUtc = now;
            order.IsDeliveryEstimated = true;
            foreach (var schedule in order.DeliverySchedules)
            {
                schedule.Status = OrderStatus.Delivered.ToString();
            }
            TrackStatusChange(id, oldStatus, OrderStatus.Delivered, now);
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                completed++;
            }
            catch (DbUpdateConcurrencyException exception)
            {
                _logger.LogInformation(exception, "Delivery transition skipped because order {OrderId} changed concurrently.", id);
                _dbContext.ChangeTracker.Clear();
            }
        }
        return completed;
    }

    private void TrackStatusChange(int orderId, OrderStatus oldStatus, OrderStatus newStatus, DateTimeOffset now)
    {
        _dbContext.AuditLogs.Add(new AuditLog
        {
            Entity = "Order",
            EntityId = orderId,
            Field = "Status",
            OldValue = oldStatus.ToString(),
            NewValue = newStatus.ToString(),
            CreatedAt = DateTime.SpecifyKind(now.UtcDateTime, DateTimeKind.Unspecified)
        });
    }

    private static DeliveryLifecycleDto Map(Order order) => new()
    {
        OrderId = order.Id,
        Status = order.Status.ToString(),
        EnRouteAtUtc = order.EnRouteAtUtc,
        ExpectedDeliveryDurationHours = order.ExpectedDeliveryDurationHours,
        ExpectedDeliveryAtUtc = order.ExpectedDeliveryAtUtc,
        DeliveredAtUtc = order.DeliveredAtUtc,
        IsDeliveryEstimated = order.IsDeliveryEstimated
    };
}
