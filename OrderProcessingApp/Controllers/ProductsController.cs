using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Services;

namespace OrderProcessingApp.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IProductService _productService;

    public ProductsController(AppDbContext dbContext, IProductService productService)
    {
        _dbContext = dbContext;
        _productService = productService;
    }

    [HttpGet]
    public async Task<ActionResult<List<ProductDto>>> GetAll(CancellationToken cancellationToken)
    {
        var products = await _dbContext.Products
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new ProductDto
            {
                Id = x.Id,
                Name = x.Name,
                SKUCode = x.SKUCode,
                PalletConversionRate = x.PalletConversionRate,
                RequiresAttention = x.RequiresAttention
            })
            .ToListAsync(cancellationToken);

        return Ok(products);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ProductDto>> GetById(int id, CancellationToken cancellationToken)
    {
        var product = await _dbContext.Products
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new ProductDto
            {
                Id = x.Id,
                Name = x.Name,
                SKUCode = x.SKUCode,
                PalletConversionRate = x.PalletConversionRate,
                RequiresAttention = x.RequiresAttention
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (product is null)
        {
            return NotFound();
        }

        return Ok(product);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var deleted = await _productService.SoftDeleteProductAsync(id, cancellationToken);
        if (!deleted)
        {
            return NotFound(new { message = $"Product not found. ProductId={id}." });
        }

        return NoContent();
    }
}
