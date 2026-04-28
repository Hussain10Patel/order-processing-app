namespace OrderProcessingApp.Models;

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SKUCode { get; set; } = string.Empty;
    public decimal PalletConversionRate { get; set; }
    public bool IsMapped { get; set; } = true;
    public bool RequiresAttention { get; set; }
    public DateTime CreatedAt { get; set; }

    public ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();
    public ICollection<PriceList> PriceLists { get; set; } = new List<PriceList>();
    public ICollection<ProductionPlan> ProductionPlans { get; set; } = new List<ProductionPlan>();
}
