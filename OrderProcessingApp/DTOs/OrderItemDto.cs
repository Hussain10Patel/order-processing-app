namespace OrderProcessingApp.DTOs;

public class OrderItemDto
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public string SKUCode { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Pallets { get; set; }
    public decimal LineTotal { get; set; }
    public bool IsUnmapped { get; set; }
    public bool IsPriceMissing { get; set; }
    public bool IsPriceMismatch { get; set; }
    public bool IsCsvPrice { get; set; }
}
