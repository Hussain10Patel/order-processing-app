using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Services;

namespace OrderProcessingApp.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StockController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IStockService _stockService;

    public StockController(AppDbContext dbContext, IStockService stockService)
    {
        _dbContext = dbContext;
        _stockService = stockService;
    }

    [HttpGet]
    public async Task<ActionResult<List<StockDto>>> GetAll(CancellationToken cancellationToken)
    {
        var rows = await _dbContext.Products
            .AsNoTracking()
            .GroupJoin(
                _dbContext.Stocks.AsNoTracking(),
                p => p.Id,
                s => s.ProductId,
                (product, stocks) => new { product, stock = stocks.OrderByDescending(x => x.LastUpdated).FirstOrDefault() })
            .OrderBy(x => x.product.Name)
            .Select(x => new StockDto
            {
                ProductId = x.product.Id,
                ProductName = x.product.Name,
                ProductCode = x.product.SKUCode,
                Quantity = x.stock != null ? x.stock.Quantity : 0,
                LastUpdated = x.stock != null ? x.stock.LastUpdated : null
            })
            .ToListAsync(cancellationToken);

        return Ok(rows);
    }

    [HttpPost("update")]
    public async Task<ActionResult<StockDto>> Update([FromBody] StockUpdateDto dto, CancellationToken cancellationToken)
    {
        try
        {
            var updated = await _stockService.UpdateManualStockAsync(dto, cancellationToken);
            return Ok(updated);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }
}
