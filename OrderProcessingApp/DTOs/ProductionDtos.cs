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
    public List<ProductionOrderDto> Orders { get; set; } = new();
}

public class ProductionOrderDto
{
    public int OrderId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public DateTime DeliveryDate { get; set; }
    public int DistributionCentreId { get; set; }
    public string DistributionCentre { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsProcessed { get; set; }
    public List<ProductionOrderItemDto> Items { get; set; } = new();
}

public class ProductionOrderItemDto
{
    public int OrderItemId { get; set; }
    public int ProductId { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal Pallets { get; set; }
    public decimal CurrentStock { get; set; }
    public decimal RequiredStock { get; set; }
    public decimal Difference { get; set; }
    public decimal ProductionRequired { get; set; }
    public decimal RemainingStock { get; set; }
    public bool? DecisionIsSufficient { get; set; }
    public decimal? DecisionRequiredProductionQty { get; set; }
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

public class ProductionCalendarItemDto
{
    public int OrderId { get; set; }
    public int OrderItemId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal Pallets { get; set; }
    public int DistributionCentreId { get; set; }
    public string DistributionCentreName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string ScheduleStatus { get; set; } = string.Empty;
    public bool IsScheduled { get; set; }
    public bool IsProcessed { get; set; }
    public bool HasProductionDecision { get; set; }
    public bool? DecisionIsSufficient { get; set; }
    public decimal? RequiredProductionQty { get; set; }
    public bool HasCurrentProductionCalculation { get; set; }
    public decimal CurrentRequiredProductionQty { get; set; }
    public decimal CurrentRemainingStock { get; set; }
    public decimal CurrentStock { get; set; }
    public decimal CurrentRequiredStock { get; set; }
}

public class ProductionCalendarDayDto
{
    public string Date { get; set; } = string.Empty;
    public List<ProductionCalendarItemDto> ScheduledItems { get; set; } = new();
    public List<ProductionCalendarItemDto> UnscheduledItems { get; set; } = new();
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

public class ProductionDecisionItemDto
{
    [Range(1, int.MaxValue)]
    public int OrderItemId { get; set; }

    public bool IsSufficient { get; set; }

    [Range(typeof(decimal), "0", "999999999")]
    public decimal RequiredProductionQty { get; set; }

    [Range(typeof(decimal), "0", "999999999", ErrorMessage = "Manual initial stock must be zero or greater.")]
    public decimal? ManualInitialStock { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }
}

public class SaveProductionDecisionsDto
{
    [Range(1, int.MaxValue)]
    public int OrderId { get; set; }

    [MinLength(1, ErrorMessage = "At least one production decision is required.")]
    public List<ProductionDecisionItemDto> Decisions { get; set; } = new();
}

public class ProductionDecisionResultDto
{
    public int OrderId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int DecisionsRecorded { get; set; }
    public int TotalOrderItems { get; set; }
    public bool IsProcessed { get; set; }
    public bool WasReopenedForEdit { get; set; }
    public List<ProductionDecisionLineResultDto> Lines { get; set; } = new();
}

public class ProductionDecisionLineResultDto
{
    public int OrderItemId { get; set; }
    public decimal CurrentStock { get; set; }
    public decimal RequiredStock { get; set; }
    public decimal RemainingStock { get; set; }
    public decimal Difference { get; set; }
    public decimal RequiredProductionQty { get; set; }
    public bool IsSufficient { get; set; }
}
