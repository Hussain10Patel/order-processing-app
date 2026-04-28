using System.ComponentModel.DataAnnotations;

namespace OrderProcessingApp.DTOs;

public class OrderAdjustmentItemDto
{
    [Range(1, int.MaxValue)]
    public int ProductId { get; set; }

    [Range(0.01, 999999999.0)]
    public decimal Quantity { get; set; }

    [Range(0.01, 999999999.0)]
    public decimal? Price { get; set; }
}

public class AdjustOrderDto
{
    [MinLength(1)]
    public List<OrderAdjustmentItemDto> Items { get; set; } = new();

    [StringLength(1000)]
    public string? Notes { get; set; }
}
