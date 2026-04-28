namespace OrderProcessingApp.Models;

public class DistributionCentre
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public int RegionId { get; set; }
    public bool RequiresAttention { get; set; }

    public Region? Region { get; set; }
    public ICollection<Order> Orders { get; set; } = new List<Order>();
    public ICollection<PriceList> PriceLists { get; set; } = new List<PriceList>();
}
