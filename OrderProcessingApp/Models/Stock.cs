namespace OrderProcessingApp.Models;

public class Stock
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public decimal Quantity { get; set; }
    public DateTime LastUpdated { get; set; }

    public Product? Product { get; set; }
}
