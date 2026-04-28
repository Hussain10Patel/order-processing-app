namespace OrderProcessingApp.DTOs;

public class ProductDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SKUCode { get; set; } = string.Empty;
    public decimal PalletConversionRate { get; set; }
    public bool RequiresAttention { get; set; }
}
