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
    private readonly IPricingService _pricingService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdminController> _logger;

    public AdminController(AppDbContext dbContext, IAdminService adminService, IOrderService orderService, IPricingService pricingService, IConfiguration configuration, ILogger<AdminController> logger)
    {
        _dbContext = dbContext;
        _adminService = adminService;
        _orderService = orderService;
        _pricingService = pricingService;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("reset-data")]
    public async Task<IActionResult> ResetData(CancellationToken cancellationToken)
    {
        // Safety gate: disabled by default in all environments.
        if (!_configuration.GetValue<bool>("Features:EnableResetData"))
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
        try
        {
            var (entity, restored) = await _adminService.CreateOrRestoreProductAsync(
                dto.Name,
                dto.SKUCode,
                dto.PalletConversionRate,
                cancellationToken);

            var output = new ProductDto
            {
                Id = entity.Id,
                Name = entity.Name,
                SKUCode = entity.SKUCode,
                PalletConversionRate = entity.PalletConversionRate,
                RequiresAttention = entity.RequiresAttention
            };

            if (restored)
            {
                return Ok(output);
            }

            return CreatedAtAction(nameof(GetProducts), new { id = entity.Id }, output);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
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
        var deleted = await _adminService.SoftDeleteProductAsync(id, cancellationToken);
        if (!deleted)
        {
            return NotFound();
        }

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

        try
        {
            var (dc, _) = await _adminService.CreateOrRestoreDistributionCentreAsync(name, input.DistributionCentreId, cancellationToken);
            return Ok(new
            {
                id = dc.Id,
                name = dc.Name
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
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
    public async Task<ActionResult<List<PriceListDto>>> GetPriceLists([FromQuery(Name = "distributionCentreIds")] string? distributionCentreIds, CancellationToken cancellationToken)
    {
        var parsedDistributionCentreIds = ParseDistributionCentreIds(distributionCentreIds);
        var priceLists = await _pricingService.GetPriceListsAsync(parsedDistributionCentreIds, null, cancellationToken);

        return Ok(priceLists);
    }

    [HttpGet("price-promotions")]
    public async Task<ActionResult<List<PricePromotionDto>>> GetPricePromotions([FromQuery] bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        var promotions = await _pricingService.GetPricePromotionsAsync(null, includeInactive, cancellationToken);
        return Ok(promotions);
    }

    [HttpPost("price-promotions")]
    public async Task<ActionResult<PricePromotionDto>> UpsertPricePromotion([FromBody] PricePromotionUpsertDto dto, CancellationToken cancellationToken)
    {
        try
        {
            var promotion = await _pricingService.UpsertPromotionAsync(dto, cancellationToken);
            return Ok(promotion);
        }
        catch (KeyNotFoundException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("price-promotions/{id:int}")]
    public async Task<ActionResult<PricePromotionDto>> UpdatePricePromotion(int id, [FromBody] PricePromotionUpsertDto dto, CancellationToken cancellationToken)
    {
        try
        {
            var promotion = await _pricingService.UpdatePromotionAsync(id, dto, cancellationToken);
            return Ok(promotion);
        }
        catch (KeyNotFoundException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("price-promotions/{id:int}")]
    public async Task<IActionResult> DeletePricePromotion(int id, CancellationToken cancellationToken)
    {
        var deleted = await _pricingService.DeletePromotionAsync(id, cancellationToken);
        if (!deleted)
        {
            return NotFound();
        }

        return NoContent();
    }

    [HttpPost("price-promotions/{id:int}/deactivate")]
    public async Task<IActionResult> DeactivatePricePromotion(int id, CancellationToken cancellationToken)
    {
        var deactivated = await _pricingService.DeactivatePromotionAsync(id, cancellationToken);
        if (!deactivated)
        {
            return NotFound();
        }

        return NoContent();
    }

    [HttpPut("pricelists/{id:int}")]
    public async Task<ActionResult<PriceListDto>> UpdatePriceList(int id, [FromBody] PriceListUpsertDto dto, CancellationToken cancellationToken)
    {
        try
        {
            var updated = await _adminService.UpdatePriceListAsync(id, dto.Price, cancellationToken);

            var output = await _dbContext.PriceLists
                .AsNoTracking()
                .Include(x => x.Product)
                .Include(x => x.DistributionCentre)
                .Where(x => x.Id == updated.Id)
                .Select(x => new PriceListDto
                {
                    Id = x.Id,
                    ProductId = x.ProductId,
                    ProductName = x.Product!.Name,
                    DistributionCentreId = x.DistributionCentreId,
                    DistributionCentreName = x.DistributionCentre!.Name,
                    BasePrice = x.Price,
                    EffectivePrice = x.Price
                })
                .FirstAsync(cancellationToken);

            return Ok(output);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new
            {
                errorCode = "PRICE_LIST_NOT_FOUND",
                message = ex.Message,
                priceListId = id,
                productId = dto.ProductId,
                distributionCentreId = dto.DistributionCentreId,
                distributionCentreIds = dto.DistributionCentreIds
            });
        }
    }

    [HttpPost("pricelists")]
    public async Task<ActionResult<object>> UpsertPriceList([FromBody] PriceListUpsertDto dto, CancellationToken cancellationToken)
    {
        try
        {
            var distributionCentreIds = dto.DistributionCentreIds is { Count: > 0 }
                ? dto.DistributionCentreIds
                : dto.DistributionCentreId.HasValue
                    ? new List<int> { dto.DistributionCentreId.Value }
                    : new List<int>();

            if (distributionCentreIds.Count == 1)
            {
                var (existing, _) = await _adminService.CreateOrRestorePriceListAsync(
                    dto.ProductId,
                    distributionCentreIds[0],
                    dto.Price,
                    cancellationToken);

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
                        BasePrice = x.Price,
                        EffectivePrice = x.Price
                    })
                    .FirstAsync(cancellationToken);

                return Ok(output);
            }

            var saved = await _adminService.ApplyPriceToDistributionCentresAsync(
                dto.ProductId,
                distributionCentreIds,
                dto.Price,
                cancellationToken);

            var ids = saved.Ids;
            var bulkOutput = await _dbContext.PriceLists
                .AsNoTracking()
                .Include(x => x.Product)
                .Include(x => x.DistributionCentre)
                .Where(x => ids.Contains(x.Id))
                .OrderBy(x => x.DistributionCentre!.Name)
                .Select(x => new PriceListDto
                {
                    Id = x.Id,
                    ProductId = x.ProductId,
                    ProductName = x.Product!.Name,
                    DistributionCentreId = x.DistributionCentreId,
                    DistributionCentreName = x.DistributionCentre!.Name,
                    BasePrice = x.Price,
                    EffectivePrice = x.Price
                })
                .ToListAsync(cancellationToken);

            var createdRows = bulkOutput.Where(x => saved.CreatedIds.Contains(x.Id)).ToList();
            var restoredRows = bulkOutput.Where(x => saved.RestoredIds.Contains(x.Id)).ToList();
            var updatedRows = bulkOutput.Where(x => saved.UpdatedIds.Contains(x.Id)).ToList();

            return Ok(new
            {
                created = createdRows,
                restored = restoredRows,
                updated = updatedRows,
                count = saved.Count,
                createdCount = saved.CreatedCount,
                restoredCount = saved.RestoredCount,
                updatedCount = saved.UpdatedCount
            });
        }
        catch (KeyNotFoundException ex)
        {
            if (string.Equals(ex.Message, "Product not found.", StringComparison.Ordinal))
            {
                return NotFound(new
                {
                    errorCode = "PRICE_LIST_PRODUCT_NOT_FOUND",
                    message = ex.Message,
                    productId = dto.ProductId,
                    distributionCentreId = dto.DistributionCentreId,
                    distributionCentreIds = dto.DistributionCentreIds
                });
            }

            if (ex.Message.StartsWith("Invalid distribution centre", StringComparison.Ordinal))
            {
                return NotFound(new
                {
                    errorCode = "PRICE_LIST_DISTRIBUTION_CENTRE_NOT_FOUND",
                    message = ex.Message,
                    productId = dto.ProductId,
                    distributionCentreId = dto.DistributionCentreId,
                    distributionCentreIds = dto.DistributionCentreIds
                });
            }

            return BadRequest(new
            {
                errorCode = "PRICE_LIST_CREATE_BAD_REQUEST",
                message = ex.Message,
                productId = dto.ProductId,
                distributionCentreId = dto.DistributionCentreId,
                distributionCentreIds = dto.DistributionCentreIds
            });
        }
        catch (InvalidOperationException ex)
        {
            if (string.Equals(ex.Message, "This item already exists", StringComparison.Ordinal))
            {
                return Conflict(new
                {
                    errorCode = "PRICE_LIST_DUPLICATE_ACTIVE",
                    message = ex.Message,
                    productId = dto.ProductId,
                    distributionCentreId = dto.DistributionCentreId,
                    distributionCentreIds = dto.DistributionCentreIds
                });
            }

            return BadRequest(new
            {
                errorCode = "PRICE_LIST_CREATE_INVALID_OPERATION",
                message = ex.Message,
                productId = dto.ProductId,
                distributionCentreId = dto.DistributionCentreId,
                distributionCentreIds = dto.DistributionCentreIds
            });
        }
    }

    [HttpDelete("pricelists/{id:int}")]
    [HttpDelete("/api/pricelists/{id:int}")]
    public async Task<IActionResult> DeletePriceList(int id, CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await _adminService.SoftDeletePriceListAsync(id, cancellationToken);
            if (!deleted)
            {
                Console.WriteLine($"[DELETE] Entity: PriceList, Id: {id}, Success: false");
                return NotFound(new { message = $"Price list not found. PriceListId={id}." });
            }

            Console.WriteLine($"[DELETE] Entity: PriceList, Id: {id}, Success: true");
            return Ok();
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine($"[DELETE] Entity: PriceList, Id: {id}, Success: false");
            return BadRequest(new { message = "Cannot delete, entity is in use" });
        }
    }

    [HttpGet("order-item-swap-audit")]
    public async Task<ActionResult<OrderItemSwapAuditResponseDto>> AuditOrderItemSwapCandidates(
        [FromQuery] bool onlyConfirmed = false,
        [FromQuery] int limit = 500,
        [FromQuery] string? orderNumber = null,
        [FromQuery] string? sku = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _orderService.AuditHistoricalSwappedOrderItemsAsync(
            onlyConfirmed,
            limit,
            orderNumber,
            sku,
            cancellationToken);
        return Ok(result);
    }

    [HttpPost("order-item-swap-repair")]
    public async Task<ActionResult<OrderItemSwapRepairResponseDto>> RepairOrderItemSwaps(
        [FromQuery] bool dryRun = true,
        [FromQuery] int limit = 500,
        [FromQuery] string? orderNumber = null,
        [FromQuery] string? sku = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _orderService.RepairHistoricalSwappedOrderItemsAsync(
            dryRun,
            limit,
            orderNumber,
            sku,
            cancellationToken);
        return Ok(result);
    }

    [HttpDelete("distributioncentres/{id:int}")]
    [HttpDelete("/api/distributioncentres/{id:int}")]
    public async Task<IActionResult> DeleteDistributionCentre(int id, CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await _adminService.SoftDeleteDistributionCentreAsync(id, cancellationToken);
            if (!deleted)
            {
                Console.WriteLine($"[DELETE] Entity: DistributionCentre, Id: {id}, Success: false");
                return NotFound(new { message = $"Distribution centre not found. DistributionCentreId={id}." });
            }

            Console.WriteLine($"[DELETE] Entity: DistributionCentre, Id: {id}, Success: true");
            return Ok();
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine($"[DELETE] Entity: DistributionCentre, Id: {id}, Success: false");
            return BadRequest(new { message = "Cannot delete, entity is in use" });
        }
    }

    private static IReadOnlyCollection<int>? ParseDistributionCentreIds(string? distributionCentreIds)
    {
        if (string.IsNullOrWhiteSpace(distributionCentreIds))
        {
            return null;
        }

        var values = distributionCentreIds
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.TryParse(value, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        return values.Count == 0 ? null : values;
    }

}
