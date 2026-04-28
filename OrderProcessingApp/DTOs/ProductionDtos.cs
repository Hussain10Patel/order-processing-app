using System.ComponentModel.DataAnnotations;

namespace OrderProcessingApp.DTOs;

public class ProductionRequestDto
{
    [MinLength(1, ErrorMessage = "At least one OrderId is required.")]
    public List<int> OrderIds { get; set; } = new();
}

public class ProductionDto
{
    public int ProductId { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string DistributionCentre { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal TotalQuantity { get; set; }
    public decimal TotalPallets { get; set; }
    public decimal OpeningStock { get; set; }
    public decimal ProductionRequired { get; set; }
}

public class ProductionResponseDto
{
    public List<ProductionDto> Scheduled { get; set; } = new();
    public List<ProductionDto> Unscheduled { get; set; } = new();
}

public class ProductionPlanUpsertDto
{
    [Range(1, int.MaxValue)]
    public int ProductId { get; set; }

    [Required]
    public DateTime Date { get; set; }

    [Range(typeof(decimal), "0", "999999999")]
    public decimal OpeningStock { get; set; }

    [Range(typeof(decimal), "0", "999999999")]
    public decimal ProductionQuantity { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }
}

public class ProductionPlanDto
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public decimal OpeningStock { get; set; }
    public decimal ProductionQuantity { get; set; }
    public decimal TotalOrderDemand { get; set; }
    public decimal ClosingStock { get; set; }
    public bool HasInsufficientStock { get; set; }
    public string? Notes { get; set; }
}

public class StockCheckDto
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public decimal RequiredQuantity { get; set; }
    public decimal AvailableQuantity { get; set; }
    public decimal Shortfall { get; set; }
    public bool IsSufficient { get; set; }
}
