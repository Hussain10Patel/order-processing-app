namespace OrderProcessingApp.Models;

public class PricePromotion
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public int DistributionCentreId { get; set; }
    public decimal PromoPrice { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

    public Product? Product { get; set; }
    public DistributionCentre? DistributionCentre { get; set; }
}
