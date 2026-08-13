using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;

namespace OrderProcessingApp.Services;

public class PricingService : IPricingService
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<PricingService> _logger;

    public PricingService(AppDbContext dbContext, ILogger<PricingService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<PriceLookupResult> GetPriceAsync(int productId, int distributionCentreId, CancellationToken cancellationToken = default)
    {
        var effective = await GetEffectivePriceAsync(productId, distributionCentreId, null, cancellationToken);
        return new PriceLookupResult
        {
            IsFound = effective.IsFound,
            Price = effective.EffectivePrice
        };
    }

    public async Task<EffectivePriceResult> GetEffectivePriceAsync(int productId, int distributionCentreId, DateTime? asOfDate = null, CancellationToken cancellationToken = default)
    {
        var results = await GetEffectivePricesAsync(new[] { (productId, distributionCentreId) }, asOfDate, cancellationToken);
        return results.TryGetValue((productId, distributionCentreId), out var result)
            ? result
            : new EffectivePriceResult { IsFound = false, EffectivePrice = null, BasePrice = null, PromoPrice = null };
    }

    public async Task<Dictionary<(int ProductId, int DistributionCentreId), EffectivePriceResult>> GetEffectivePricesAsync(IEnumerable<(int ProductId, int DistributionCentreId)> keys, DateTime? asOfDate = null, CancellationToken cancellationToken = default)
    {
        var distinctKeys = keys.Distinct().ToList();
        if (distinctKeys.Count == 0)
        {
            return new Dictionary<(int ProductId, int DistributionCentreId), EffectivePriceResult>();
        }

        var effectiveDate = DateTime.SpecifyKind((asOfDate ?? DateTime.UtcNow).Date, DateTimeKind.Unspecified);
        var productIds = distinctKeys.Select(x => x.ProductId).Distinct().ToList();
        var distributionCentreIds = distinctKeys.Select(x => x.DistributionCentreId).Distinct().ToList();

        var priceEntries = await _dbContext.PriceLists
            .AsNoTracking()
            .Where(priceList => productIds.Contains(priceList.ProductId) && distributionCentreIds.Contains(priceList.DistributionCentreId))
            .ToListAsync(cancellationToken);

        var basePriceLookup = priceEntries
            .GroupBy(x => (x.ProductId, x.DistributionCentreId))
            .ToDictionary(x => x.Key, x => (decimal?)x.First().Price);

        var promoEntries = await _dbContext.PricePromotions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(promoEntry => productIds.Contains(promoEntry.ProductId) && distributionCentreIds.Contains(promoEntry.DistributionCentreId))
            .ToListAsync(cancellationToken);

        var activePromoLookup = promoEntries
            .Where(promoEntry => promoEntry.IsActive && promoEntry.StartDate <= effectiveDate && promoEntry.EndDate >= effectiveDate)
            .GroupBy(promoEntry => (promoEntry.ProductId, promoEntry.DistributionCentreId))
            .ToDictionary(
                x => x.Key,
                x => x.OrderByDescending(promoEntry => promoEntry.CreatedAt).ThenByDescending(promoEntry => promoEntry.Id).First());

        var latestPromoLookup = promoEntries
            .GroupBy(promoEntry => (promoEntry.ProductId, promoEntry.DistributionCentreId))
            .ToDictionary(
                x => x.Key,
                x => x.OrderByDescending(promoEntry => promoEntry.CreatedAt).ThenByDescending(promoEntry => promoEntry.Id).First());

        var results = new Dictionary<(int ProductId, int DistributionCentreId), EffectivePriceResult>();
        foreach (var pair in distinctKeys)
        {
            var price = basePriceLookup.TryGetValue(pair, out var basePrice) ? basePrice : null;
            activePromoLookup.TryGetValue(pair, out var activePromo);
            latestPromoLookup.TryGetValue(pair, out var latestPromo);
            var effectivePrice = activePromo?.PromoPrice ?? price;

            _logger.LogInformation(
                "Effective price selection. ProductId={ProductId}, DistributionCentreId={DistributionCentreId}, EffectivePrice={EffectivePrice}, Source={Source}",
                pair.ProductId,
                pair.DistributionCentreId,
                effectivePrice,
                activePromo is not null ? "promo" : "base");

            results[pair] = new EffectivePriceResult
            {
                IsFound = effectivePrice.HasValue,
                EffectivePrice = effectivePrice,
                BasePrice = price,
                PromoPrice = activePromo?.PromoPrice,
                PromoStartDate = activePromo?.StartDate,
                PromoEndDate = activePromo?.EndDate,
                IsPromoApplied = activePromo is not null,
                IsPromoActive = activePromo is not null,
                IsPromoExpired = latestPromo is not null && latestPromo.EndDate < effectiveDate
            };
        }

        return results;
    }

    public async Task<List<PriceListDto>> GetPriceListsAsync(IReadOnlyCollection<int>? distributionCentreIds = null, DateTime? asOfDate = null, CancellationToken cancellationToken = default)
    {
        var effectiveDate = DateTime.SpecifyKind((asOfDate ?? DateTime.UtcNow).Date, DateTimeKind.Unspecified);

        var query = _dbContext.PriceLists
            .AsNoTracking()
            .Include(x => x.Product)
            .Include(x => x.DistributionCentre)
            .AsQueryable();

        if (distributionCentreIds is not null && distributionCentreIds.Count > 0)
        {
            query = query.Where(x => distributionCentreIds.Contains(x.DistributionCentreId));
        }

        var basePrices = await query
            .OrderBy(x => x.DistributionCentre!.Name)
            .ThenBy(x => x.Product!.Name)
            .ToListAsync(cancellationToken);

        var productIds = basePrices.Select(x => x.ProductId).Distinct().ToList();
        var dcIds = basePrices.Select(x => x.DistributionCentreId).Distinct().ToList();

        var promos = await _dbContext.PricePromotions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x =>
                productIds.Contains(x.ProductId)
                && dcIds.Contains(x.DistributionCentreId))
            .ToListAsync(cancellationToken);

        var activePromoLookup = promos
            .Where(x => x.IsActive && x.StartDate <= effectiveDate && x.EndDate >= effectiveDate)
            .GroupBy(x => new { x.ProductId, x.DistributionCentreId })
            .ToDictionary(
                x => (x.Key.ProductId, x.Key.DistributionCentreId),
                x => x.OrderByDescending(p => p.CreatedAt).First());

        var latestPromoLookup = promos
            .GroupBy(x => new { x.ProductId, x.DistributionCentreId })
            .ToDictionary(
                x => (x.Key.ProductId, x.Key.DistributionCentreId),
                x => x.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id).First());

        return basePrices.Select(x =>
        {
            activePromoLookup.TryGetValue((x.ProductId, x.DistributionCentreId), out var promo);
            latestPromoLookup.TryGetValue((x.ProductId, x.DistributionCentreId), out var latestPromo);

            return new PriceListDto
            {
                Id = x.Id,
                ProductId = x.ProductId,
                ProductName = x.Product?.Name ?? string.Empty,
                DistributionCentreId = x.DistributionCentreId,
                DistributionCentreName = x.DistributionCentre?.Name ?? string.Empty,
                BasePrice = x.Price,
                PromoId = promo?.Id,
                PromoPrice = promo?.PromoPrice,
                EffectivePrice = promo?.PromoPrice ?? x.Price,
                PromoStartDate = promo?.StartDate,
                PromoEndDate = promo?.EndDate,
                IsPromoActive = promo is not null,
                IsPromoExpired = latestPromo is not null && latestPromo.EndDate < effectiveDate
            };
        }).ToList();
    }

    public async Task<List<PricePromotionDto>> GetPricePromotionsAsync(DateTime? asOfDate = null, bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        var effectiveDate = DateTime.SpecifyKind((asOfDate ?? DateTime.UtcNow).Date, DateTimeKind.Unspecified);

        var query = _dbContext.PricePromotions
            .AsNoTracking()
            .Include(x => x.Product)
            .Include(x => x.DistributionCentre)
            .AsQueryable();

        if (includeInactive)
        {
            query = query.IgnoreQueryFilters();
        }

        var promotions = await query
            .OrderBy(x => x.Product!.Name)
            .ThenBy(x => x.DistributionCentre!.Name)
            .ThenBy(x => x.StartDate)
            .ThenByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        return promotions.Select(x => new PricePromotionDto
        {
            Id = x.Id,
            ProductId = x.ProductId,
            ProductName = x.Product?.Name ?? string.Empty,
            DistributionCentreId = x.DistributionCentreId,
            DistributionCentreName = x.DistributionCentre?.Name ?? string.Empty,
            PromoPrice = x.PromoPrice,
            StartDate = x.StartDate,
            EndDate = x.EndDate,
            IsActive = x.IsActive,
            IsPromoActive = x.IsActive && x.StartDate <= effectiveDate && x.EndDate >= effectiveDate,
            IsPromoExpired = x.EndDate < effectiveDate,
            CreatedAt = x.CreatedAt
        }).ToList();
    }

    public async Task<PricePromotionDto> UpsertPromotionAsync(PricePromotionUpsertDto dto, CancellationToken cancellationToken = default)
    {
        var start = DateTime.SpecifyKind(dto.StartDate.Date, DateTimeKind.Unspecified);
        var end = DateTime.SpecifyKind(dto.EndDate.Date, DateTimeKind.Unspecified);
        if (end < start)
        {
            throw new InvalidOperationException("Promo end date cannot be earlier than start date.");
        }

        var productExists = await _dbContext.Products.AsNoTracking().AnyAsync(x => x.Id == dto.ProductId, cancellationToken);
        if (!productExists)
        {
            throw new KeyNotFoundException("Product not found.");
        }

        var distributionCentreExists = await _dbContext.DistributionCentres.AsNoTracking().AnyAsync(x => x.Id == dto.DistributionCentreId, cancellationToken);
        if (!distributionCentreExists)
        {
            throw new KeyNotFoundException("Distribution centre not found.");
        }

        var promotion = await _dbContext.PricePromotions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x =>
                x.ProductId == dto.ProductId
                && x.DistributionCentreId == dto.DistributionCentreId
                && x.StartDate == start
                && x.EndDate == end,
                cancellationToken);

        if (promotion is null)
        {
            promotion = new PricePromotion
            {
                ProductId = dto.ProductId,
                DistributionCentreId = dto.DistributionCentreId,
                PromoPrice = dto.PromoPrice,
                StartDate = start,
                EndDate = end,
                IsActive = dto.IsActive,
                CreatedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified)
            };

            _dbContext.PricePromotions.Add(promotion);
        }
        else
        {
            promotion.PromoPrice = dto.PromoPrice;
            promotion.IsActive = dto.IsActive;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return await BuildPromotionDtoAsync(promotion.Id, null, cancellationToken);
    }

    public async Task<PricePromotionDto> UpdatePromotionAsync(int id, PricePromotionUpsertDto dto, CancellationToken cancellationToken = default)
    {
        var start = DateTime.SpecifyKind(dto.StartDate.Date, DateTimeKind.Unspecified);
        var end = DateTime.SpecifyKind(dto.EndDate.Date, DateTimeKind.Unspecified);
        if (end < start)
        {
            throw new InvalidOperationException("Promo end date cannot be earlier than start date.");
        }

        var productExists = await _dbContext.Products.AsNoTracking().AnyAsync(x => x.Id == dto.ProductId, cancellationToken);
        if (!productExists)
        {
            throw new KeyNotFoundException("Product not found.");
        }

        var distributionCentreExists = await _dbContext.DistributionCentres.AsNoTracking().AnyAsync(x => x.Id == dto.DistributionCentreId, cancellationToken);
        if (!distributionCentreExists)
        {
            throw new KeyNotFoundException("Distribution centre not found.");
        }

        var promotion = await _dbContext.PricePromotions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (promotion is null)
        {
            throw new KeyNotFoundException("Promo not found.");
        }

        var duplicate = await _dbContext.PricePromotions
            .IgnoreQueryFilters()
            .AnyAsync(x =>
                x.Id != id
                && x.ProductId == dto.ProductId
                && x.DistributionCentreId == dto.DistributionCentreId
                && x.StartDate == start
                && x.EndDate == end,
                cancellationToken);

        if (duplicate)
        {
            throw new InvalidOperationException("A promo with this exact window already exists.");
        }

        promotion.ProductId = dto.ProductId;
        promotion.DistributionCentreId = dto.DistributionCentreId;
        promotion.PromoPrice = dto.PromoPrice;
        promotion.StartDate = start;
        promotion.EndDate = end;
        promotion.IsActive = dto.IsActive;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return await BuildPromotionDtoAsync(promotion.Id, null, cancellationToken);
    }

    public async Task<bool> DeletePromotionAsync(int id, CancellationToken cancellationToken = default)
    {
        return await DeactivatePromotionAsync(id, cancellationToken);
    }

    public async Task<bool> DeactivatePromotionAsync(int id, CancellationToken cancellationToken = default)
    {
        var promotion = await _dbContext.PricePromotions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (promotion is null)
        {
            return false;
        }

        promotion.IsActive = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<PricePromotionDto> BuildPromotionDtoAsync(int promotionId, DateTime? asOfDate, CancellationToken cancellationToken)
    {
        var effectiveDate = DateTime.SpecifyKind((asOfDate ?? DateTime.UtcNow).Date, DateTimeKind.Unspecified);

        var promotion = await _dbContext.PricePromotions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(x => x.Product)
            .Include(x => x.DistributionCentre)
            .FirstAsync(x => x.Id == promotionId, cancellationToken);

        return new PricePromotionDto
        {
            Id = promotion.Id,
            ProductId = promotion.ProductId,
            ProductName = promotion.Product?.Name ?? string.Empty,
            DistributionCentreId = promotion.DistributionCentreId,
            DistributionCentreName = promotion.DistributionCentre?.Name ?? string.Empty,
            PromoPrice = promotion.PromoPrice,
            StartDate = promotion.StartDate,
            EndDate = promotion.EndDate,
            IsActive = promotion.IsActive,
            IsPromoActive = promotion.IsActive && promotion.StartDate <= effectiveDate && promotion.EndDate >= effectiveDate,
            IsPromoExpired = promotion.EndDate < effectiveDate,
            CreatedAt = promotion.CreatedAt
        };
    }
}
