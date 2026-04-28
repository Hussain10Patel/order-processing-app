namespace OrderProcessingApp.Models;

public class ProductionPlan
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public DateTime Date { get; set; }
    public decimal OpeningStock { get; set; }
    public decimal ProductionQuantity { get; set; }
    public decimal ClosingStock { get; set; }
    public string? Notes { get; set; }

    public Product? Product { get; set; }
}
