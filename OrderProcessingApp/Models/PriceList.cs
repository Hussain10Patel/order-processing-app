namespace OrderProcessingApp.Models;

public class PriceList
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public int DistributionCentreId { get; set; }
    public decimal Price { get; set; }

    public Product? Product { get; set; }
    public DistributionCentre? DistributionCentre { get; set; }
}
