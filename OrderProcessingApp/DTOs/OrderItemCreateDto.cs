using System.ComponentModel.DataAnnotations;

namespace OrderProcessingApp.DTOs;

public class OrderItemCreateDto
{
    [Range(1, int.MaxValue)]
    public int ProductId { get; set; }

    public string? ProductCode { get; set; }

    public string? ProductName { get; set; }

    [Range(0.01, 999999999.0)]
    public decimal Quantity { get; set; }

    public decimal? Price { get; set; }

    public bool IsUnmapped { get; set; }

    public Dictionary<string, string> Metadata { get; set; } = new();
}
