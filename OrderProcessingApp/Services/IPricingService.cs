using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;

namespace OrderProcessingApp.Services;

public class PriceLookupResult
{
    public bool IsFound { get; set; }
    public decimal? Price { get; set; }
}

public class EffectivePriceResult
{
    public bool IsFound { get; set; }
    public decimal? EffectivePrice { get; set; }
    public decimal? BasePrice { get; set; }
    public decimal? PromoPrice { get; set; }
    public DateTime? PromoStartDate { get; set; }
    public DateTime? PromoEndDate { get; set; }
    public bool IsPromoApplied { get; set; }
    public bool IsPromoActive { get; set; }
    public bool IsPromoExpired { get; set; }
}

public interface IPricingService
{
    Task<PriceLookupResult> GetPriceAsync(int productId, int distributionCentreId, CancellationToken cancellationToken = default);
    Task<EffectivePriceResult> GetEffectivePriceAsync(int productId, int distributionCentreId, DateTime? asOfDate = null, CancellationToken cancellationToken = default);
    Task<List<PriceListDto>> GetPriceListsAsync(IReadOnlyCollection<int>? distributionCentreIds = null, DateTime? asOfDate = null, CancellationToken cancellationToken = default);
    Task<List<PricePromotionDto>> GetPricePromotionsAsync(DateTime? asOfDate = null, bool includeInactive = false, CancellationToken cancellationToken = default);
    Task<PricePromotionDto> UpsertPromotionAsync(PricePromotionUpsertDto dto, CancellationToken cancellationToken = default);
    Task<PricePromotionDto> UpdatePromotionAsync(int id, PricePromotionUpsertDto dto, CancellationToken cancellationToken = default);
    Task<bool> DeletePromotionAsync(int id, CancellationToken cancellationToken = default);
    Task<bool> DeactivatePromotionAsync(int id, CancellationToken cancellationToken = default);
}
