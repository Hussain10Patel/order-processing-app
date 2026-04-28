using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;
using OrderProcessingApp.Services;

namespace OrderProcessingApp.Controllers;

[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IAdminService _adminService;
    private readonly IOrderService _orderService;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<AdminController> _logger;

    public AdminController(AppDbContext dbContext, IAdminService adminService, IOrderService orderService, IWebHostEnvironment environment, ILogger<AdminController> logger)
    {
        _dbContext = dbContext;
        _adminService = adminService;
        _orderService = orderService;
        _environment = environment;
        _logger = logger;
    }

    [HttpPost("reset-data")]
    public async Task<IActionResult> ResetData(CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        await _adminService.ResetDataAsync(cancellationToken);
        return Ok(new { message = "Test data reset successfully." });
    }

    [HttpGet("products")]
    public async Task<ActionResult<List<ProductDto>>> GetProducts(CancellationToken cancellationToken)
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

    [HttpPost("products")]
    public async Task<ActionResult<ProductDto>> CreateProduct([FromBody] ProductUpsertDto dto, CancellationToken cancellationToken)
    {
        var normalizedSku = NormalizeSku(dto.SKUCode);
        var exists = await _dbContext.Products
            .AnyAsync(x => x.SKUCode != null && x.SKUCode.Trim().ToLower() == normalizedSku, cancellationToken);
        if (exists)
        {
            return BadRequest(new { message = "A product with this SKU already exists." });
        }

        var entity = new Product
        {
            Name = dto.Name,
            SKUCode = normalizedSku,
            PalletConversionRate = dto.PalletConversionRate,
            IsMapped = true,
            RequiresAttention = false
        };

        _dbContext.Products.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetProducts), new { id = entity.Id }, new ProductDto
        {
            Id = entity.Id,
            Name = entity.Name,
            SKUCode = entity.SKUCode,
            PalletConversionRate = entity.PalletConversionRate,
            RequiresAttention = entity.RequiresAttention
        });
    }

    [HttpPut("products/{id:int}")]
    public async Task<ActionResult<ProductDto>> UpdateProduct(int id, [FromBody] ProductUpsertDto dto, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.Products.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null)
        {
            return NotFound();
        }

        var normalizedSku = NormalizeSku(dto.SKUCode);
        var skuConflict = await _dbContext.Products
            .AnyAsync(x => x.Id != id && x.SKUCode != null && x.SKUCode.Trim().ToLower() == normalizedSku, cancellationToken);
        if (skuConflict)
        {
            return BadRequest(new { message = "A product with this SKU already exists." });
        }

        entity.Name = dto.Name;
        entity.SKUCode = normalizedSku;
        entity.PalletConversionRate = dto.PalletConversionRate;
        entity.IsMapped = true;
        entity.RequiresAttention = false;

        await _dbContext.SaveChangesAsync(cancellationToken);

        var affectedOrderIds = await _dbContext.OrderItems
            .AsNoTracking()
            .Where(item => item.ProductId == entity.Id || (item.ProductCode != null && item.ProductCode.Trim().ToLower() == normalizedSku))
            .Select(item => item.OrderId)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var orderId in affectedOrderIds)
        {
            await _orderService.RecalculateOrderAsync(orderId, cancellationToken);
            _logger.LogInformation("Auto recalculated Order {OrderId} after product mapping {ProductSku}", orderId, entity.SKUCode);
        }

        return Ok(new ProductDto
        {
            Id = entity.Id,
            Name = entity.Name,
            SKUCode = entity.SKUCode,
            PalletConversionRate = entity.PalletConversionRate,
            RequiresAttention = entity.RequiresAttention
        });
    }

    [HttpDelete("products/{id:int}")]
    public async Task<IActionResult> DeleteProduct(int id, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.Products.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null)
        {
            return NotFound();
        }

        _dbContext.Products.Remove(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("regions")]
    public async Task<ActionResult<List<RegionDto>>> GetRegions(CancellationToken cancellationToken)
    {
        var regions = await _dbContext.Regions
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new RegionDto
            {
                Id = x.Id,
                Name = x.Name
            })
            .ToListAsync(cancellationToken);

        return Ok(regions);
    }

    [HttpGet("distributioncentres")]
    public async Task<IActionResult> GetDistributionCentres(CancellationToken cancellationToken)
    {
        var data = await _dbContext.DistributionCentres
            .AsNoTracking()
            .Select(x => new
            {
                id = x.Id,
                name = x.Name
            })
            .ToListAsync(cancellationToken);

        return Ok(data);
    }

    [HttpPost("distributioncentres")]
    public async Task<IActionResult> CreateDistributionCentre([FromBody] CreateDistributionCentreDto input, CancellationToken cancellationToken)
    {
        var name = input.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new { message = "Distribution centre name is required." });
        }

        var exists = await _dbContext.DistributionCentres
            .AsNoTracking()
            .AnyAsync(x => x.Name == name || x.Code == name, cancellationToken);
        if (exists)
        {
            return BadRequest(new { message = "Distribution centre already exists." });
        }

        var sourceDistributionCentreId = input.DistributionCentreId;
        if (!sourceDistributionCentreId.HasValue || sourceDistributionCentreId.Value <= 0)
        {
            sourceDistributionCentreId = await _dbContext.DistributionCentres
                .AsNoTracking()
                .OrderBy(x => x.Id)
                .Select(x => (int?)x.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (!sourceDistributionCentreId.HasValue)
        {
            return BadRequest(new { message = "DistributionCentreId is required." });
        }

        var sourceDistributionCentre = await _dbContext.DistributionCentres
            .AsNoTracking()
            .Where(x => x.Id == sourceDistributionCentreId.Value)
            .Select(x => new { x.RegionId })
            .FirstOrDefaultAsync(cancellationToken);

        if (sourceDistributionCentre is null)
        {
            return BadRequest(new { message = "Invalid distribution centre." });
        }

        var dc = new DistributionCentre
        {
            Name = name,
            Code = name,
            RegionId = sourceDistributionCentre.RegionId,
            RequiresAttention = false
        };

        _dbContext.DistributionCentres.Add(dc);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            id = dc.Id,
            name = dc.Name
        });
    }

    [HttpPost("regions")]
    public async Task<ActionResult<RegionDto>> CreateRegion([FromBody] CreateRegionDto dto, CancellationToken cancellationToken)
    {
        var exists = await _dbContext.Regions.AnyAsync(x => x.Name == dto.Name, cancellationToken);
        if (exists)
        {
            return BadRequest(new { message = "Region already exists." });
        }

        var region = new Region
        {
            Name = dto.Name
        };

        _dbContext.Regions.Add(region);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new RegionDto
        {
            Id = region.Id,
            Name = region.Name
        });
    }

    private static string NormalizeSku(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().ToLowerInvariant();
    }

    [HttpGet("pricelists")]
    public async Task<ActionResult<List<PriceListDto>>> GetPriceLists(CancellationToken cancellationToken)
    {
        var priceLists = await _dbContext.PriceLists
            .AsNoTracking()
            .Include(x => x.Product)
            .Include(x => x.DistributionCentre)
            .OrderBy(x => x.DistributionCentre!.Name)
            .ThenBy(x => x.Product!.Name)
            .Select(x => new PriceListDto
            {
                Id = x.Id,
                ProductId = x.ProductId,
                ProductName = x.Product!.Name,
                DistributionCentreId = x.DistributionCentreId,
                DistributionCentreName = x.DistributionCentre!.Name,
                Price = x.Price
            })
            .ToListAsync(cancellationToken);

        return Ok(priceLists);
    }

    [HttpPost("pricelists")]
    public async Task<ActionResult<PriceListDto>> UpsertPriceList([FromBody] PriceListUpsertDto dto, CancellationToken cancellationToken)
    {
        var productExists = await _dbContext.Products.AnyAsync(x => x.Id == dto.ProductId, cancellationToken);
        var dc = await _dbContext.DistributionCentres
            .FirstOrDefaultAsync(x => x.Id == dto.DistributionCentreId, cancellationToken);

        if (!productExists)
        {
            return BadRequest(new { message = "Product not found." });
        }

        if (dc is null)
        {
            return BadRequest(new { message = "Invalid distribution centre" });
        }

        var existing = await _dbContext.PriceLists
            .FirstOrDefaultAsync(x => x.ProductId == dto.ProductId && x.DistributionCentreId == dto.DistributionCentreId, cancellationToken);

        if (existing is null)
        {
            existing = new PriceList
            {
                ProductId = dto.ProductId,
                DistributionCentreId = dto.DistributionCentreId,
                Price = dto.Price
            };

            _dbContext.PriceLists.Add(existing);
        }
        else
        {
            existing.Price = dto.Price;
        }

        Console.WriteLine($"Saving PriceList: Product={dto.ProductId}, DC={dto.DistributionCentreId}, Price={dto.Price}");

        await _dbContext.SaveChangesAsync(cancellationToken);

        var output = await _dbContext.PriceLists
            .AsNoTracking()
            .Include(x => x.Product)
            .Include(x => x.DistributionCentre)
            .Where(x => x.Id == existing.Id)
            .Select(x => new PriceListDto
            {
                Id = x.Id,
                ProductId = x.ProductId,
                ProductName = x.Product!.Name,
                DistributionCentreId = x.DistributionCentreId,
                DistributionCentreName = x.DistributionCentre!.Name,
                Price = x.Price
            })
            .FirstAsync(cancellationToken);

        return Ok(output);
    }

}
