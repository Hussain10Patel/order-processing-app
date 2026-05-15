using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;

namespace OrderProcessingApp.Services;

public interface IStockService
{
    Task<Dictionary<int, Stock>> GetByProductIdsAsync(IEnumerable<int> productIds, CancellationToken cancellationToken = default);
    Task<StockDto> UpdateManualStockAsync(StockUpdateDto dto, CancellationToken cancellationToken = default);
}

public sealed class StockService : IStockService
{
    private readonly AppDbContext _dbContext;

    public StockService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Dictionary<int, Stock>> GetByProductIdsAsync(IEnumerable<int> productIds, CancellationToken cancellationToken = default)
    {
        var ids = productIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<int, Stock>();
        }

        return await _dbContext.Stocks
            .Where(x => ids.Contains(x.ProductId))
            .ToDictionaryAsync(x => x.ProductId, cancellationToken);
    }

    public async Task<StockDto> UpdateManualStockAsync(StockUpdateDto dto, CancellationToken cancellationToken = default)
    {
        var product = await _dbContext.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == dto.ProductId, cancellationToken);

        if (product is null)
        {
            throw new KeyNotFoundException($"Product not found. ProductId={dto.ProductId}.");
        }

        var stock = await _dbContext.Stocks
            .FirstOrDefaultAsync(x => x.ProductId == dto.ProductId, cancellationToken);

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        if (stock is null)
        {
            stock = new Stock
            {
                ProductId = dto.ProductId,
                Quantity = dto.Quantity,
                LastUpdated = now
            };

            _dbContext.Stocks.Add(stock);
        }
        else
        {
            stock.Quantity = dto.Quantity;
            stock.LastUpdated = now;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new StockDto
        {
            ProductId = product.Id,
            ProductName = product.Name,
            ProductCode = product.SKUCode,
            Quantity = stock.Quantity,
            LastUpdated = stock.LastUpdated
        };
    }
}
