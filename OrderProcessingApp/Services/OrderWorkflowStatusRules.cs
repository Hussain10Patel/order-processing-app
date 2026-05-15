using OrderProcessingApp.Models;

namespace OrderProcessingApp.Services;

public static class OrderWorkflowStatusRules
{
    public static readonly OrderStatus[] ProductionAndDeliveryQueryableStatuses =
    {
        OrderStatus.Approved,
        OrderStatus.InProduction,
        OrderStatus.Processed
    };

    public static readonly OrderStatus[] ProductionDemandQueryableStatuses =
    {
        OrderStatus.Approved,
        OrderStatus.InProduction,
        OrderStatus.Processed
    };

    public static readonly OrderStatus[] ProductionDecisionEditableStatuses =
    {
        OrderStatus.Approved,
        OrderStatus.InProduction,
        OrderStatus.Processed
    };

    public static readonly OrderStatus[] DeliveryEligibleStatuses =
    {
        OrderStatus.Approved,
        OrderStatus.InProduction,
        OrderStatus.Processed
    };

    public static string ProductionAndDeliveryStatusLabel => "Approved,InProduction,Processed";
    public static string ProductionDemandStatusLabel => "Approved,InProduction,Processed";
    public static string DeliveryEligibleStatusLabel => "Approved,InProduction,Processed";

    private static readonly HashSet<OrderStatus> ProductionAndDeliveryStatuses = new()
    {
        OrderStatus.Approved,
        OrderStatus.InProduction,
        OrderStatus.Processed
    };

    private static readonly HashSet<OrderStatus> DeliveryEligibleStatusSet = new()
    {
        OrderStatus.Approved,
        OrderStatus.InProduction,
        OrderStatus.Processed
    };

    public static bool IsProductionVisible(OrderStatus status) => ProductionAndDeliveryStatuses.Contains(status);

    public static bool IsDeliveryVisible(OrderStatus status) => DeliveryEligibleStatusSet.Contains(status);

    public static bool IsDeliveryEligible(OrderStatus status) => DeliveryEligibleStatusSet.Contains(status);

    public static bool IsProductionDecisionEditable(OrderStatus status) => ProductionDecisionEditableStatuses.Contains(status);
}
