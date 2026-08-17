using System.ComponentModel.DataAnnotations;

namespace OrderProcessingApp.DTOs;

public class ProductionDeliveryPlanProductDto
{
    public int ProductId { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
}

public class ProductionDeliveryPlanProductQuantityDto
{
    [Range(1, int.MaxValue)]
    public int ProductId { get; set; }

    public decimal Quantity { get; set; }
}

public class ProductionDeliveryPlanStockValueDto
{
    public int ProductId { get; set; }
    public decimal Quantity { get; set; }
}

public class ProductionDeliveryPlanEventDto
{
    public int Id { get; set; }
    public int Sequence { get; set; }
    public string EventType { get; set; } = string.Empty;
    public int? OrderId { get; set; }
    public string? OrderNumber { get; set; }
    public int? DistributionCentreId { get; set; }
    public string? DistributionCentreName { get; set; }
    public string? OrderDate { get; set; }
    public string? PlannedDeliveryDate { get; set; }
    public bool IsScheduled { get; set; }
    public string ScheduleStatus { get; set; } = string.Empty;
    public bool CanSchedule { get; set; }
    public List<ProductionDeliveryPlanProductQuantityDto> ProductQuantities { get; set; } = new();
    public List<ProductionDeliveryPlanStockValueDto> StockBefore { get; set; } = new();
    public List<ProductionDeliveryPlanStockValueDto> StockAfter { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ProductionDeliveryPlanDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<ProductionDeliveryPlanProductDto> Products { get; set; } = new();
    public List<ProductionDeliveryPlanEventDto> Events { get; set; } = new();
}

public class ProductionDeliveryPlanQuantitiesUpdateDto
{
    [MinLength(1, ErrorMessage = "At least one product quantity is required.")]
    public List<ProductionDeliveryPlanProductQuantityDto> Quantities { get; set; } = new();
}

public class ProductionDeliveryPlanDeliveryDateUpdateDto
{
    public DateTime? DeliveryDate { get; set; }
}

public class ProductionDeliveryPlanMoveEventDto
{
    public int? AfterEventId { get; set; }
}