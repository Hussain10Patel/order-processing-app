namespace OrderProcessingApp.DTOs;

public class CsvOrderRowDto
{
    public string FileName { get; set; } = string.Empty;
    public int RowNumber { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public DateTime OrderDate { get; set; }
    public DateTime DeliveryDate { get; set; }
    public string DistributionCentre { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}
