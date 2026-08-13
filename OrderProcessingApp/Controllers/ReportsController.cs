using System.Text;
using Microsoft.AspNetCore.Mvc;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Services;

namespace OrderProcessingApp.Controllers;

[ApiController]
[Route("api/reports")]
public class ReportsController : ControllerBase
{
    private readonly IReportService _reportService;
    private readonly ILogger<ReportsController> _logger;

    public ReportsController(IReportService reportService, ILogger<ReportsController> logger)
    {
        _reportService = reportService;
        _logger = logger;
    }

    // ── Dashboard (JSON) ───────────────────────────────────────────────────

    [HttpGet]
    public async Task<ActionResult<object>> Get(CancellationToken cancellationToken)
    {
        var orders = await _reportService.GetOrdersReportAsync(cancellationToken);
        var sales = await _reportService.GetSalesSummaryAsync(cancellationToken);
        return Ok(new { orders, sales });
    }

    // ── Summary/report endpoints (JSON + CSV), not detailed operational exports ──

    [HttpGet("available-dates")]
    public async Task<ActionResult<List<ReportAvailableDateDto>>> AvailableDates(CancellationToken cancellationToken)
    {
        var dates = await _reportService.GetAvailableReportDatesAsync(cancellationToken);
        return Ok(dates);
    }

    [HttpGet("summary-data")]
    public async Task<ActionResult<ReportSummaryDto>> SummaryData(
        [FromQuery] DateTime? date,
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        CancellationToken cancellationToken)
    {
        // Safe precedence: range parameters override the single-date query when provided.
        if (fromDate.HasValue || toDate.HasValue)
        {
            if (!fromDate.HasValue || !toDate.HasValue)
            {
                return BadRequest(new { message = "Both fromDate and toDate are required when requesting a date range." });
            }

            if (fromDate.Value.Date > toDate.Value.Date)
            {
                return BadRequest(new { message = "From date must be on or before To date." });
            }

            var result = await _reportService.GetSummaryByDeliveryDateRangeAsync(fromDate.Value, toDate.Value, cancellationToken);
            return Ok(result);
        }

        if (!date.HasValue)
        {
            return BadRequest(new { message = "A date or a valid fromDate/toDate range is required." });
        }

        var singleDateResult = await _reportService.GetSummaryByDeliveryDateAsync(date.Value, cancellationToken);
        return Ok(singleDateResult);
    }

    [HttpGet("summary")]
    [HttpGet("summary-csv")]
    [Produces("text/csv")]
    public async Task<IActionResult> Summary([FromQuery] DateTime date, CancellationToken cancellationToken)
    {
        var result = await _reportService.GetSummaryByDeliveryDateAsync(date, cancellationToken);

        var csv = new StringBuilder();
        csv.AppendLine("PoNumber,Dc,DeliveryDate,Status");
        foreach (var row in result.DeliverySummary)
        {
            csv.AppendLine(string.Join(",", new[]
            {
                Escape(row.PoNumber),
                Escape(row.Dc),
                Escape(row.DeliveryDate),
                Escape(row.Status)
            }));
        }

        var rowCount = result.DeliverySummary.Count;
        _logger.LogInformation("[EXPORT] Generated file, rows: {RowCount}", rowCount);
        return File(BuildCsvBytes(csv), "text/csv", $"summary-{date:yyyy-MM-dd}.csv");
    }

    [HttpGet("supplier-summary")]
    [HttpGet("supplier-summary-csv")]
    [Produces("text/csv")]
    public async Task<IActionResult> SupplierSummary([FromQuery] DateTime date, CancellationToken cancellationToken)
    {
        var result = await _reportService.GetSupplierSummaryAsync(date, cancellationToken);

        var csv = new StringBuilder();
        csv.AppendLine("DistributionCentre,OrderNumber,OrderDate,DeliveryDate,TotalPallets");
        foreach (var group in result)
        {
            foreach (var order in group.Orders)
            {
                csv.AppendLine(string.Join(",", new[]
                {
                    Escape(order.DistributionCentre),
                    Escape(order.OrderNumber),
                    Escape(order.OrderDate),
                    Escape(order.DeliveryDate),
                    order.TotalPallets.ToString("0.##")
                }));
            }
        }

        var rowCount = result.Sum(g => g.Orders.Count);
        _logger.LogInformation("[EXPORT] Generated file, rows: {RowCount}", rowCount);
        return File(BuildCsvBytes(csv), "text/csv", $"supplier-summary-{date:yyyy-MM-dd}.csv");
    }

    [HttpGet("supplier-delivery")]
    [HttpGet("supplier-delivery-csv")]
    [Produces("text/csv")]
    public async Task<IActionResult> SupplierDelivery([FromQuery] DateTime? date, CancellationToken cancellationToken)
    {
        var result = await _reportService.GetSupplierDeliveryAsync(date, cancellationToken);

        var csv = new StringBuilder();
        csv.AppendLine("OrderNumber,OrderDate,DistributionCentre,DeliveryDate,TotalPallets");
        foreach (var row in result)
        {
            csv.AppendLine(string.Join(",", new[]
            {
                Escape(row.OrderNumber),
                Escape(row.OrderDate),
                Escape(row.DistributionCentre),
                Escape(row.DeliveryDate),
                row.TotalPallets.ToString("0.##")
            }));
        }

        var rowCount = result.Count;
        _logger.LogInformation("[EXPORT] Generated file, rows: {RowCount}", rowCount);
        var dateSuffix = date.HasValue ? date.Value.ToString("yyyy-MM-dd") : "all";
        return File(BuildCsvBytes(csv), "text/csv", $"supplier-delivery-{dateSuffix}.csv");
    }

    [HttpGet("daily-delivery")]
    [HttpGet("daily-delivery-csv")]
    [Produces("text/csv")]
    public async Task<IActionResult> DailyDelivery([FromQuery] DateTime date, CancellationToken cancellationToken)
    {
        var result = await _reportService.GetDailyDeliveryReportAsync(date, cancellationToken);

        var csv = new StringBuilder();
        csv.AppendLine("DistributionCentre,OrderNumber,Products,TotalPallets");
        foreach (var group in result)
        {
            foreach (var delivery in group.Deliveries)
            {
                csv.AppendLine(string.Join(",", new[]
                {
                    Escape(group.DistributionCentre),
                    Escape(delivery.OrderNumber),
                    Escape(string.Join("; ", delivery.ProductSummary)),
                    delivery.TotalPallets.ToString("0.##")
                }));
            }
        }

        var rowCount = result.Sum(g => g.Deliveries.Count);
        _logger.LogInformation("[EXPORT] Generated file, rows: {RowCount}", rowCount);
        return File(BuildCsvBytes(csv), "text/csv", $"daily-delivery-{date:yyyy-MM-dd}.csv");
    }

    [HttpGet("orders")]
    [HttpGet("orders-summary-csv")]
    [Produces("text/csv")]
    public async Task<IActionResult> OrdersReport(CancellationToken cancellationToken)
    {
        var result = await _reportService.GetOrdersReportAsync(cancellationToken);

        var csv = new StringBuilder();
        csv.AppendLine("Status,Count,TotalValue");
        foreach (var row in result.ByStatus)
        {
            csv.AppendLine(string.Join(",", new[]
            {
                Escape(row.Status),
                row.Count.ToString(),
                row.TotalValue.ToString("0.00")
            }));
        }

        var rowCount = result.ByStatus.Count;
        _logger.LogInformation("[EXPORT] Generated file, rows: {RowCount}", rowCount);
        return File(BuildCsvBytes(csv), "text/csv", "orders-report.csv");
    }

    [HttpGet("sales")]
    [HttpGet("sales-summary-csv")]
    [Produces("text/csv")]
    public async Task<IActionResult> SalesReport(CancellationToken cancellationToken)
    {
        var result = await _reportService.GetSalesSummaryAsync(cancellationToken);

        var csv = new StringBuilder();
        csv.AppendLine("ProductName,SKUCode,TotalQuantity,TotalRevenue,TotalPallets");
        foreach (var row in result.ByProduct)
        {
            csv.AppendLine(string.Join(",", new[]
            {
                Escape(row.ProductName),
                Escape(row.SKUCode),
                row.TotalQuantity.ToString("0.##"),
                row.TotalRevenue.ToString("0.00"),
                row.TotalPallets.ToString("0.##")
            }));
        }

        var rowCount = result.ByProduct.Count;
        _logger.LogInformation("[EXPORT] Generated file, rows: {RowCount}", rowCount);
        return File(BuildCsvBytes(csv), "text/csv", "sales-report.csv");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static byte[] BuildCsvBytes(StringBuilder csv)
    {
        var bom = Encoding.UTF8.GetPreamble();
        var payload = Encoding.UTF8.GetBytes(csv.ToString());
        var result = new byte[bom.Length + payload.Length];
        Buffer.BlockCopy(bom, 0, result, 0, bom.Length);
        Buffer.BlockCopy(payload, 0, result, bom.Length, payload.Length);
        return result;
    }

    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
