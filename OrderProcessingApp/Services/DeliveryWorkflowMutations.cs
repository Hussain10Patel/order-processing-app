using OrderProcessingApp.Data;
using OrderProcessingApp.Models;

namespace OrderProcessingApp.Services;

public static class DeliveryWorkflowMutations
{
    public static void EnsureNotDispatched(Order order, DateTimeOffset? now = null)
    {
        if (order.Status is OrderStatus.EnRoute or OrderStatus.Delivered
            || (order.Status == OrderStatus.Scheduled && order.EnRouteAtUtc.HasValue
                && order.EnRouteAtUtc.Value <= (now ?? DateTimeOffset.UtcNow)))
        {
            throw new InvalidOperationException("En Route and Delivered orders cannot be reassigned, rescheduled, unassigned or unscheduled.");
        }
    }

    public static void UpdateScheduledDate(Order order, DateTime? date, TimeZoneInfo? zone = null)
    {
        if (order.DeliveryDate != date && order.EnRouteAtUtc.HasValue && date.HasValue)
        {
            zone ??= TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg");
            var time = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(order.EnRouteAtUtc.Value, zone).DateTime);
            var departure = DeliveryClock.LocalDepartureTime(date.Value, time, zone);
            order.ExpectedDeliveryAtUtc = DeliveryClock.ExpectedDeliveryTime(departure, order.ExpectedDeliveryDurationHours);
            order.EnRouteAtUtc = departure;
        }
        order.DeliveryDate = date;
    }

    public static void ClearAssignmentAndSchedule(AppDbContext dbContext, Order order, DateTimeOffset? now = null)
    {
        EnsureNotDispatched(order, now);
        dbContext.DeliverySchedules.RemoveRange(order.DeliverySchedules);
        order.DeliverySchedules.Clear();
        order.IsAssignedToProduction = false;
        order.EnRouteAtUtc = null;
        order.ExpectedDeliveryAtUtc = null;
        order.ExpectedDeliveryDurationHours = null;
        if (order.Status == OrderStatus.Scheduled)
        {
            order.Status = OrderStatus.Approved;
        }
    }
}
