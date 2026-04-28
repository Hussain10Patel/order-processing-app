using OrderProcessingApp.Models;

namespace OrderProcessingApp.Services;

/// <summary>
/// Domain helper for order status logic.
/// Provides methods to determine validation state and normalize statuses for workflow decisions.
/// Preserves backward compatibility while enforcing clean workflow transitions.
/// </summary>
public static class OrderStatusHelper
{
    /// <summary>
    /// Determines if an order is validated (no pricing or quantity issues).
    /// </summary>
    public static bool IsOrderValidated(Order order)
    {
        if (order == null)
        {
            return false;
        }

        // Check if any item has pricing or quantity issues
        var hasPricingIssues = order.Items.Any(item =>
            item.IsPriceMissing || item.IsPriceMismatch);

        var hasQuantityIssues = order.Items.Any(item =>
            item.Quantity <= 0);

        return !hasPricingIssues && !hasQuantityIssues;
    }

    /// <summary>
    /// Gets the effective status for workflow decisions.
    /// Maps overlapping statuses (Validated, Approved) to "Approved" for transition logic.
    /// Preserves Flagged, Processed for clear workflow paths.
    /// </summary>
    public static OrderStatus GetEffectiveStatus(Order order)
    {
        if (order == null)
        {
            return OrderStatus.Pending;
        }

        // Terminal statuses are final
        if (order.Status == OrderStatus.Processed)
        {
            return OrderStatus.Processed;
        }

        // Both Approved and Validated represent "validated" state for workflow purposes
        if (order.Status == OrderStatus.Approved || order.Status == OrderStatus.Validated)
        {
            return OrderStatus.Approved;
        }

        // Flagged and Pending pass through unchanged
        return order.Status;
    }

    /// <summary>
    /// Validates if a transition from current status to target status is allowed.
    /// </summary>
    public static bool IsValidTransition(OrderStatus currentStatus, OrderStatus targetStatus)
    {
        // Effective status mapping for validation
        var currentEffective = MapToEffectiveStatus(currentStatus);
        var targetEffective = MapToEffectiveStatus(targetStatus);

        return currentEffective switch
        {
            OrderStatus.Pending => targetEffective == OrderStatus.Flagged || targetEffective == OrderStatus.Approved,
            OrderStatus.Flagged => targetEffective == OrderStatus.Approved,
            OrderStatus.Validated => targetEffective == OrderStatus.Approved,
            OrderStatus.Approved => targetEffective == OrderStatus.Processed,
            OrderStatus.Processed => false, // Processed is terminal
            _ => false
        };
    }

    /// <summary>
    /// Internal helper to map status to effective status for transition validation.
    /// </summary>
    private static OrderStatus MapToEffectiveStatus(OrderStatus status)
    {
        return status switch
        {
            OrderStatus.Validated => OrderStatus.Approved,
            _ => status
        };
    }
}
