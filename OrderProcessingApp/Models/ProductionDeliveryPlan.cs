namespace OrderProcessingApp.Models;

public class ProductionDeliveryPlan
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<ProductionDeliveryPlanEvent> Events { get; set; } = new List<ProductionDeliveryPlanEvent>();
}