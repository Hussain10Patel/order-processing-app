using OrderProcessingApp.Models;

namespace OrderProcessingApp.Services;

public static class OrderWorkflowStatusRules
{
    public static readonly OrderStatus[] InvoiceExportStatuses =
    {
        OrderStatus.Approved,
        OrderStatus.Processed,
        OrderStatus.Scheduled,
        OrderStatus.EnRoute,
        OrderStatus.Delivered
    };

    public static readonly OrderStatus[] ProductionAndDeliveryQueryableStatuses =
    {
        OrderStatus.Approved,
        OrderStatus.InProduction,
        OrderStatus.Processed,
        OrderStatus.Scheduled,
        OrderStatus.EnRoute,
        OrderStatus.Delivered
    };

    public static readonly OrderStatus[] ProductionDemandQueryableStatuses =
    {
        OrderStatus.Approved,
        OrderStatus.InProduction,
        OrderStatus.Processed,
        OrderStatus.Scheduled,
        OrderStatus.EnRoute,
        OrderStatus.Delivered
    };

    public static readonly OrderStatus[] ProductionDecisionEditableStatuses =
    {
        OrderStatus.Approved,
        OrderStatus.InProduction,
        OrderStatus.Processed,
        OrderStatus.Scheduled
    };

    public static readonly OrderStatus[] DeliveryEligibleStatuses =
    {
        OrderStatus.Approved,
        OrderStatus.InProduction,
        OrderStatus.Processed,
        OrderStatus.Scheduled
    };

    public static string ProductionAndDeliveryStatusLabel => string.Join(",", ProductionAndDeliveryQueryableStatuses);
    public static string ProductionDemandStatusLabel => string.Join(",", ProductionDemandQueryableStatuses);
    public static string DeliveryEligibleStatusLabel => string.Join(",", DeliveryEligibleStatuses);

    public static bool IsProductionVisible(OrderStatus status) => ProductionAndDeliveryQueryableStatuses.Contains(status);

    public static bool IsDeliveryVisible(OrderStatus status) => ProductionAndDeliveryQueryableStatuses.Contains(status);

    public static bool IsDeliveryEligible(OrderStatus status) => DeliveryEligibleStatuses.Contains(status);

    public static bool IsProductionDecisionEditable(OrderStatus status) => ProductionDecisionEditableStatuses.Contains(status);
}
