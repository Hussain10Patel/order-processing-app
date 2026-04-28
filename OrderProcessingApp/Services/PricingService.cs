using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;

namespace OrderProcessingApp.Services;

public class PricingService : IPricingService
{
    private readonly AppDbContext _dbContext;

    public PricingService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<PriceLookupResult> GetPriceAsync(int productId, int distributionCentreId, CancellationToken cancellationToken = default)
    {
        var price = await _dbContext.PriceLists
            .AsNoTracking()
            .Where(priceList => priceList.ProductId == productId && priceList.DistributionCentreId == distributionCentreId)
            .Select(priceList => (decimal?)priceList.Price)
            .FirstOrDefaultAsync(cancellationToken);

        return new PriceLookupResult
        {
            IsFound = price.HasValue,
            Price = price
        };
    }
}
