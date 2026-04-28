namespace OrderProcessingApp.Models;

public class PendingCsvImportSession
{
    public string FileId { get; set; } = string.Empty;
    public string RowsJson { get; set; } = string.Empty;
    public bool AllowDuplicates { get; set; }
    public string MissingDistributionCentresJson { get; set; } = string.Empty;
    public string MissingProductsJson { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}