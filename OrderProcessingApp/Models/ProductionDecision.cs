namespace OrderProcessingApp.Models;

public class ProductionDecision
{
    public int Id { get; set; }
    public int OrderItemId { get; set; }
    public bool IsSufficient { get; set; }
    public decimal RequiredStock { get; set; }
    public decimal CurrentStock { get; set; }
    public decimal Difference { get; set; }
    public decimal RequiredProductionQty { get; set; }
    public decimal RemainingStock { get; set; }
    public string? Notes { get; set; }

    public OrderItem? OrderItem { get; set; }
}
