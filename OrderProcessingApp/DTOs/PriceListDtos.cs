using System.ComponentModel.DataAnnotations;

namespace OrderProcessingApp.DTOs;

public class PriceListDto
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public int DistributionCentreId { get; set; }
    public string DistributionCentreName { get; set; } = string.Empty;
    public decimal Price { get; set; }
}

public class PriceListUpsertDto
{
    [Range(1, int.MaxValue)]
    public int ProductId { get; set; }

    [Range(1, int.MaxValue)]
    public int DistributionCentreId { get; set; }

    [Range(0.01, 999999999.0)]
    public decimal Price { get; set; }
}

public class SystemPriceLookupDto
{
    public int ProductId { get; set; }
    public int DistributionCentreId { get; set; }
    public decimal Price { get; set; }
}
