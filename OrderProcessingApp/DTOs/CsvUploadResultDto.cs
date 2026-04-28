namespace OrderProcessingApp.DTOs;

public class CsvUploadResultDto
{
    public bool Success { get; set; } = true;
    public int TotalRows { get; set; }
    public int CreatedOrders { get; set; }
    public int SkippedOrders { get; set; }
    public int UpdatedOrders { get; set; }
    public int FlaggedOrders { get; set; }
    public string? FileId { get; set; }
    public string? Type { get; set; }
    public string? Message { get; set; }
    public List<string> MissingDistributionCentres { get; set; } = new();
    public List<string> MissingProducts { get; set; } = new();
    public bool RequiresUserAction { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<CsvUploadErrorDto> ValidationErrors { get; set; } = new();
}
