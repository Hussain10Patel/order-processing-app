namespace OrderProcessingApp.DTOs;

public class CsvUploadErrorDto
{
    public string FileName { get; set; } = string.Empty;
    public int? RowNumber { get; set; }
    public string? Field { get; set; }
    public string Message { get; set; } = string.Empty;
}