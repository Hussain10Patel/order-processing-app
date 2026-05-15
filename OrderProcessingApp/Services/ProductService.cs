using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;

namespace OrderProcessingApp.Services;

public class ProductService : IProductService
{
    private readonly AppDbContext _dbContext;

    public ProductService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> SoftDeleteProductAsync(int id, CancellationToken cancellationToken = default)
    {
        var product = await _dbContext.Products
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (product is null)
        {
            return false;
        }

        product.IsActive = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
