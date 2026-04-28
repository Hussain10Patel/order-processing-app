using OrderProcessingApp.DTOs;
using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;
using OrderProcessingApp.Models;
using System.Text.Json;

namespace OrderProcessingApp.Services;

public sealed record PendingCsvImport(
    string FileId,
    List<CsvOrderRowDto> Rows,
    bool AllowDuplicates,
    List<string> MissingDistributionCentres,
    List<string> MissingProducts,
    DateTime CreatedAtUtc);

public interface IPendingCsvImportService
{
    Task<string> SaveAsync(string? fileId, List<CsvOrderRowDto> rows, bool allowDuplicates, List<string> missingDistributionCentres, List<string> missingProducts, CancellationToken cancellationToken = default);
    Task<PendingCsvImport?> GetAsync(string fileId, CancellationToken cancellationToken = default);
    Task RemoveAsync(string fileId, CancellationToken cancellationToken = default);
}

public class PendingCsvImportService : IPendingCsvImportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppDbContext _dbContext;

    public PendingCsvImportService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<string> SaveAsync(string? fileId, List<CsvOrderRowDto> rows, bool allowDuplicates, List<string> missingDistributionCentres, List<string> missingProducts, CancellationToken cancellationToken = default)
    {
        var resolvedFileId = string.IsNullOrWhiteSpace(fileId) ? Guid.NewGuid().ToString("N") : fileId;
        var session = await _dbContext.PendingCsvImportSessions
            .FirstOrDefaultAsync(item => item.FileId == resolvedFileId, cancellationToken);

        if (session is null)
        {
            session = new PendingCsvImportSession
            {
                FileId = resolvedFileId
            };

            _dbContext.PendingCsvImportSessions.Add(session);
        }

        session.RowsJson = JsonSerializer.Serialize(rows.Select(CloneRow).ToList(), JsonOptions);
        session.AllowDuplicates = allowDuplicates;
        session.MissingDistributionCentresJson = JsonSerializer.Serialize(
            missingDistributionCentres.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            JsonOptions);
        session.MissingProductsJson = JsonSerializer.Serialize(
            missingProducts.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            JsonOptions);
        session.CreatedAtUtc = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return resolvedFileId;
    }

    public async Task<PendingCsvImport?> GetAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var session = await _dbContext.PendingCsvImportSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.FileId == fileId, cancellationToken);

        if (session is null)
        {
            return null;
        }

        var rows = JsonSerializer.Deserialize<List<CsvOrderRowDto>>(session.RowsJson, JsonOptions) ?? new List<CsvOrderRowDto>();
        var missingDistributionCentres = JsonSerializer.Deserialize<List<string>>(session.MissingDistributionCentresJson, JsonOptions) ?? new List<string>();
        var missingProducts = JsonSerializer.Deserialize<List<string>>(session.MissingProductsJson, JsonOptions) ?? new List<string>();

        return new PendingCsvImport(
            session.FileId,
            rows.Select(CloneRow).ToList(),
            session.AllowDuplicates,
            missingDistributionCentres,
            missingProducts,
            session.CreatedAtUtc);
    }

    public async Task RemoveAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var session = await _dbContext.PendingCsvImportSessions
            .FirstOrDefaultAsync(item => item.FileId == fileId, cancellationToken);

        if (session is null)
        {
            return;
        }

        _dbContext.PendingCsvImportSessions.Remove(session);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static CsvOrderRowDto CloneRow(CsvOrderRowDto row)
    {
        return new CsvOrderRowDto
        {
            FileName = row.FileName,
            RowNumber = row.RowNumber,
            OrderNumber = row.OrderNumber,
            OrderDate = row.OrderDate,
            DeliveryDate = row.DeliveryDate,
            DistributionCentre = row.DistributionCentre,
            ProductCode = row.ProductCode,
            ProductName = row.ProductName,
            Product = row.Product,
            Quantity = row.Quantity,
            Price = row.Price,
            Metadata = new Dictionary<string, string>(row.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }
}