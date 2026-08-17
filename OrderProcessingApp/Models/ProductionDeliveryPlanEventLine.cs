namespace OrderProcessingApp.Models;

public class ProductionDeliveryPlanEventLine
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public int ProductId { get; set; }
    public decimal Quantity { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ProductionDeliveryPlanEvent? Event { get; set; }
    public Product? Product { get; set; }
}