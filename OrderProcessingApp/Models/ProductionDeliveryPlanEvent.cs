namespace OrderProcessingApp.Models;

public class ProductionDeliveryPlanEvent
{
    public int Id { get; set; }
    public int PlanId { get; set; }
    public int Sequence { get; set; }
    public ProductionDeliveryPlanEventType EventType { get; set; }
    public int? OrderId { get; set; }
    public int? OwnerOrderId { get; set; }
    public DateTime? PlannedDeliveryDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ProductionDeliveryPlan? Plan { get; set; }
    public Order? Order { get; set; }
    public ICollection<ProductionDeliveryPlanEventLine> Lines { get; set; } = new List<ProductionDeliveryPlanEventLine>();
}