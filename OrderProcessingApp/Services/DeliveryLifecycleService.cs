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

    public async Task<DeliveryLifecycleDto> SetEnRouteAsync(int orderId, string? departureTime, decimal? durationHours, CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.Orders.Include(x => x.DeliverySchedules)
            .FirstOrDefaultAsync(x => x.Id == orderId, cancellationToken)
            ?? throw new KeyNotFoundException($"Order not found. OrderId={orderId}.");
        if (!order.DeliveryDate.HasValue)
        {
            throw new InvalidOperationException("A saved scheduled delivery date is required before planning departure.");
        }
        var departure = _clock.DepartureTime(order.DeliveryDate.Value, departureTime);
        var expected = DeliveryClock.ExpectedDeliveryTime(departure, durationHours);

        // Saved departure is authoritative even when the worker has not yet promoted the status.
        if (order.Status == OrderStatus.EnRoute
            || (order.EnRouteAtUtc.HasValue && order.EnRouteAtUtc.Value <= _clock.UtcNow))
        {
            if (order.Status == OrderStatus.Delivered || order.ExpectedDeliveryDurationHours != durationHours
                || order.EnRouteAtUtc != departure)
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

        var previousDeparture = order.EnRouteAtUtc;
        order.EnRouteAtUtc = departure;
        order.ExpectedDeliveryDurationHours = durationHours;
        order.ExpectedDeliveryAtUtc = expected;
        order.IsDeliveryEstimated = false;
        new AuditService(_dbContext).TrackChange("Order", order.Id, "DepartureAtUtc",
            previousDeparture?.ToString("O"), departure.ToString("O"));
        _dbContext.Entry(order).Property(x => x.Status).IsModified = true;
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
            if (order.EnRouteAtUtc.HasValue != order.ExpectedDeliveryAtUtc.HasValue
                || (order.EnRouteAtUtc.HasValue && order.ExpectedDeliveryAtUtc <= order.EnRouteAtUtc)
                || (order.Status == OrderStatus.Scheduled && !order.DeliveryDate.HasValue)
                || (order.Status == OrderStatus.EnRoute && (!order.EnRouteAtUtc.HasValue || !order.ExpectedDeliveryAtUtc.HasValue)))
            {
                _logger.LogWarning("Delivery transition skipped for order {OrderId}: persisted delivery date/timing is inconsistent with status {Status}.", id, order.Status);
                continue;
            }

            var departed = order.Status == OrderStatus.Scheduled && order.EnRouteAtUtc.HasValue
                && order.EnRouteAtUtc.Value <= now;
            if (departed)
            {
                order.Status = OrderStatus.EnRoute;
                foreach (var schedule in order.DeliverySchedules) schedule.Status = OrderStatus.EnRoute.ToString();
                TrackStatusChange(id, OrderStatus.Scheduled, OrderStatus.EnRoute, now);
            }

            var isDue = order.Status == OrderStatus.Scheduled
                ? order.EnRouteAtUtc is null && order.DeliveryDate.HasValue && order.DeliveryDate.Value.Date < today
                : order.Status == OrderStatus.EnRoute && order.EnRouteAtUtc.HasValue
                    && order.ExpectedDeliveryAtUtc.HasValue && order.ExpectedDeliveryAtUtc.Value <= now;
            if (!isDue && !departed)
            {
                continue;
            }

            if (isDue)
            {
                var oldStatus = order.Status;
                order.Status = OrderStatus.Delivered;
                order.DeliveredAtUtc = now;
                order.IsDeliveryEstimated = true;
                foreach (var schedule in order.DeliverySchedules) schedule.Status = OrderStatus.Delivered.ToString();
                TrackStatusChange(id, oldStatus, OrderStatus.Delivered, now);
            }
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                if (isDue) completed++;
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
