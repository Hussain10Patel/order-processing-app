namespace OrderProcessingApp.Services;

public class PriceLookupResult
{
    public bool IsFound { get; set; }
    public decimal? Price { get; set; }
}

public interface IPricingService
{
    Task<PriceLookupResult> GetPriceAsync(int productId, int distributionCentreId, CancellationToken cancellationToken = default);
}
