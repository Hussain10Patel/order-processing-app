using OrderProcessingApp.DTOs;

namespace OrderProcessingApp.Services;

public class CsvImportProcessingResult
{
    public CsvUploadResultDto Result { get; set; } = new();
    public List<CsvOrderRowDto> PendingRows { get; set; } = new();
}