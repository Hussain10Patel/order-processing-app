using System.ComponentModel.DataAnnotations;

namespace OrderProcessingApp.DTOs;

public class StockDto
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public DateTime? LastUpdated { get; set; }
}

public class StockUpdateDto
{
    [Range(1, int.MaxValue)]
    public int ProductId { get; set; }

    [Range(typeof(decimal), "0", "999999999")]
    public decimal Quantity { get; set; }
}
