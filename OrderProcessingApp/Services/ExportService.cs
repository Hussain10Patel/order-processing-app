using System.Text;
using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;

namespace OrderProcessingApp.Services;

public class ExportService : IExportService
{
    private readonly AppDbContext _dbContext;

    public ExportService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ExportFileResult> ExportOrdersToExcelAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        var queryDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        var start = queryDate.Date;
        var end = start.AddDays(1);

        var orders = await _dbContext.Orders
            .AsNoTracking()
            .Include(x => x.DistributionCentre)
            .Include(x => x.Items)
                .ThenInclude(x => x.Product)
            .Where(x => x.DeliveryDate >= start && x.DeliveryDate < end)
            .OrderBy(x => x.OrderNumber)
            .ToListAsync(cancellationToken);

        var csv = new StringBuilder();
        csv.AppendLine("OrderNumber,OrderDate,DeliveryDate,DistributionCentre,Status,SKU,Product,Quantity,Price,Pallets");

        foreach (var order in orders)
        {
            foreach (var item in order.Items)
            {
                csv.AppendLine(string.Join(",", new[]
                {
                    EscapeCsv(order.OrderNumber),
                    order.OrderDate.ToString("yyyy-MM-dd"),
                    order.DeliveryDate.ToString("yyyy-MM-dd"),
                    EscapeCsv(order.DistributionCentre?.Name ?? string.Empty),
                    EscapeCsv(order.Status.ToString()),
                    EscapeCsv(item.Product?.SKUCode ?? string.Empty),
                    EscapeCsv(item.Product?.Name ?? string.Empty),
                    item.Quantity.ToString("0.##"),
                    item.Price.ToString("0.00"),
                    item.Pallets.ToString("0.##")
                }));
            }
        }

        return new ExportFileResult
        {
            Content = BuildUtf8CsvBytes(csv.ToString()),
            ContentType = "text/csv",
            FileName = "orders.csv"
        };
    }

    public async Task<ExportFileResult> ExportDeliveryScheduleAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        var queryDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        var start = queryDate.Date;
        var end = start.AddDays(1);

        var schedules = await _dbContext.DeliverySchedules
            .AsNoTracking()
            .Include(x => x.Order)
                .ThenInclude(x => x!.DistributionCentre)
            .Include(x => x.Order)
                .ThenInclude(x => x!.Items)
            .Where(x => x.DeliveryDate >= start && x.DeliveryDate < end)
            .OrderBy(x => x.Order!.DistributionCentre!.Name)
            .ThenBy(x => x.Order!.OrderNumber)
            .ToListAsync(cancellationToken);

        var csv = new StringBuilder();
        csv.AppendLine("DistributionCentre,OrderNumber,DeliveryDate,Status,TotalPallets,Notes");

        foreach (var schedule in schedules)
        {
            csv.AppendLine(string.Join(",", new[]
            {
                EscapeCsv(schedule.Order?.DistributionCentre?.Name ?? string.Empty),
                EscapeCsv(schedule.Order?.OrderNumber ?? string.Empty),
                schedule.DeliveryDate.ToString("yyyy-MM-dd"),
                EscapeCsv(schedule.Status),
                (schedule.Order?.Items.Sum(i => i.Pallets) ?? 0).ToString("0.##"),
                EscapeCsv(schedule.Notes ?? string.Empty)
            }));
        }

        return new ExportFileResult
        {
            Content = BuildUtf8CsvBytes(csv.ToString()),
            ContentType = "text/csv",
            FileName = "delivery.csv"
        };
    }

    private static byte[] BuildUtf8CsvBytes(string csv)
    {
        var bom = Encoding.UTF8.GetPreamble();
        var payload = Encoding.UTF8.GetBytes(csv);
        var result = new byte[bom.Length + payload.Length];

        Buffer.BlockCopy(bom, 0, result, 0, bom.Length);
        Buffer.BlockCopy(payload, 0, result, bom.Length, payload.Length);

        return result;
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
