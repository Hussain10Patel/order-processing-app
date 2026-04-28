using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;

namespace OrderProcessingApp.Services;

public class PalletService : IPalletService
{
    private readonly AppDbContext _dbContext;

    public PalletService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<decimal> CalculatePalletsAsync(int productId, decimal quantity, CancellationToken cancellationToken = default)
    {
        var product = await _dbContext.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == productId, cancellationToken);

        var conversionRate = product?.PalletConversionRate ?? 0;
        if (conversionRate <= 0)
        {
            conversionRate = 1;
        }

        return decimal.Round(quantity / conversionRate, 2);
    }
}
