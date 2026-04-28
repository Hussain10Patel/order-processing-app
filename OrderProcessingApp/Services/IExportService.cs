namespace OrderProcessingApp.Services;

public class ExportFileResult
{
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public string ContentType { get; set; } = "application/octet-stream";
    public string FileName { get; set; } = "export.bin";
}

public interface IExportService
{
    Task<ExportFileResult> ExportOrdersToExcelAsync(DateTime date, CancellationToken cancellationToken = default);
    Task<ExportFileResult> ExportDeliveryScheduleAsync(DateTime date, CancellationToken cancellationToken = default);
}
