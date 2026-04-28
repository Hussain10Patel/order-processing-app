namespace OrderProcessingApp.Services;

public interface IPastelExportService
{
    Task<ExportFileResult> GenerateInvoiceFileAsync(DateTime date, CancellationToken cancellationToken = default);
}
