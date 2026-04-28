namespace OrderProcessingApp.DTOs;

public class OrderFilterDto
{
    public int? Status { get; set; }
    public int? DistributionCentreId { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? OrderNumber { get; set; }
    public string? ProductCode { get; set; }
    public string? ProductName { get; set; }
}
