using OrderProcessingApp.Data;
using OrderProcessingApp.Models;

namespace OrderProcessingApp.Services;

public static class DeliveryWorkflowMutations
{
    public static void EnsureNotDispatched(Order order)
    {
        if (order.Status is OrderStatus.EnRoute or OrderStatus.Delivered)
        {
            throw new InvalidOperationException("En Route and Delivered orders cannot be reassigned, rescheduled, unassigned or unscheduled.");
        }
    }

    public static void ClearAssignmentAndSchedule(AppDbContext dbContext, Order order)
    {
        EnsureNotDispatched(order);
        dbContext.DeliverySchedules.RemoveRange(order.DeliverySchedules);
        order.DeliverySchedules.Clear();
        order.IsAssignedToProduction = false;
        if (order.Status == OrderStatus.Scheduled)
        {
            order.Status = OrderStatus.Approved;
        }
    }
}
