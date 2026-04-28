using OrderProcessingApp.Models;

namespace OrderProcessingApp.DTOs;

public class OrderDto
{
    public int Id { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string OrderDate { get; set; } = string.Empty;
    public string DeliveryDate { get; set; } = string.Empty;
    public int DistributionCentreId { get; set; }
    public string DistributionCentreName { get; set; } = string.Empty;
    public OrderSource Source { get; set; }
    public OrderStatus Status { get; set; }
    public string StatusLabel { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public bool IsPriceMissing { get; set; }
    public bool IsPriceMismatch { get; set; }
    public bool IsAdjusted { get; set; }
    public bool IsValidated { get; set; }
    public decimal TotalValue { get; set; }
    public decimal TotalPallets { get; set; }
    public List<OrderItemDto> Items { get; set; } = new();
}
