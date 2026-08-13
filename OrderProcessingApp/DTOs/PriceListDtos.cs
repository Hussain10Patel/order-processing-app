using System.ComponentModel.DataAnnotations;

namespace OrderProcessingApp.DTOs;

public class PriceListDto
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public int DistributionCentreId { get; set; }
    public string DistributionCentreName { get; set; } = string.Empty;
    public decimal BasePrice { get; set; }
    public int? PromoId { get; set; }
    public decimal? PromoPrice { get; set; }
    public decimal EffectivePrice { get; set; }
    public DateTime? PromoStartDate { get; set; }
    public DateTime? PromoEndDate { get; set; }
    public bool IsPromoActive { get; set; }
    public bool IsPromoExpired { get; set; }
}

public class PriceListUpsertDto
{
    [Range(1, int.MaxValue)]
    public int ProductId { get; set; }

    [Range(1, int.MaxValue)]
    public int? DistributionCentreId { get; set; }

    public List<int>? DistributionCentreIds { get; set; }

    [Range(0.01, 999999999.0)]
    public decimal Price { get; set; }
}

public class SystemPriceLookupDto
{
    public int ProductId { get; set; }
    public int DistributionCentreId { get; set; }
    public decimal Price { get; set; }
}

public class PriceListBulkApplyResult
{
    public int Count { get; set; }
    public int CreatedCount { get; set; }
    public int RestoredCount { get; set; }
    public int UpdatedCount { get; set; }
    public List<int> CreatedIds { get; set; } = new();
    public List<int> RestoredIds { get; set; } = new();
    public List<int> UpdatedIds { get; set; } = new();
    public List<int> Ids => CreatedIds.Concat(RestoredIds).Concat(UpdatedIds).Distinct().ToList();
}

public class PricePromotionUpsertDto
{
    [Range(1, int.MaxValue)]
    public int ProductId { get; set; }

    [Range(1, int.MaxValue)]
    public int DistributionCentreId { get; set; }

    [Range(0.01, 999999999.0)]
    public decimal PromoPrice { get; set; }

    [Required]
    public DateTime StartDate { get; set; }

    [Required]
    public DateTime EndDate { get; set; }

    public bool IsActive { get; set; } = true;
}

public class PricePromotionDto
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public int DistributionCentreId { get; set; }
    public string DistributionCentreName { get; set; } = string.Empty;
    public decimal PromoPrice { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool IsActive { get; set; }
    public bool IsPromoActive { get; set; }
    public bool IsPromoExpired { get; set; }
    public DateTime CreatedAt { get; set; }
}
