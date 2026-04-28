using System.Text;
using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;
using OrderProcessingApp.Models;

namespace OrderProcessingApp.Services;

public class PastelExportService : IPastelExportService
{
    private readonly AppDbContext _dbContext;

    public PastelExportService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ExportFileResult> GenerateInvoiceFileAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        var queryDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        var start = queryDate.Date;
        var end = start.AddDays(1);

        var orders = await _dbContext.Orders
            .AsNoTracking()
            .Include(x => x.DistributionCentre)
            .Include(x => x.Items)
                .ThenInclude(x => x.Product)
            .Where(x => x.DeliveryDate >= start && x.DeliveryDate < end
                && (x.Status == OrderStatus.Approved || x.Status == OrderStatus.Processed))
            .OrderBy(x => x.DistributionCentre!.Name)
            .ThenBy(x => x.OrderNumber)
            .ToListAsync(cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine("Customer,ProductSKU,Quantity,Price");

        foreach (var order in orders)
        {
            foreach (var item in order.Items)
            {
                var customer = EscapeCsv(order.DistributionCentre?.Name ?? string.Empty);
                var sku = EscapeCsv(item.Product?.SKUCode ?? string.Empty);
                sb.AppendLine($"{customer},{sku},{item.Quantity:0.##},{item.Price:0.00}");
            }
        }

        return new ExportFileResult
        {
            Content = BuildUtf8CsvBytes(sb.ToString()),
            ContentType = "text/csv",
            FileName = "pastel.csv"
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
        if (value.Contains(',') || value.Contains('"'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
