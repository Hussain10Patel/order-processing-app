using Microsoft.AspNetCore.Mvc;
using OrderProcessingApp.Services;

namespace OrderProcessingApp.Controllers;

[ApiController]
[Route("api/export")]
public class ExportController : ControllerBase
{
    private readonly IExportService _exportService;
    private readonly IPastelExportService _pastelExportService;

    public ExportController(IExportService exportService, IPastelExportService pastelExportService)
    {
        _exportService = exportService;
        _pastelExportService = pastelExportService;
    }

    // Detailed operational export (row-level order items).
    [HttpGet("orders")]
    [HttpGet("detailed/orders")]
    [Produces("text/csv")]
    public async Task<IActionResult> ExportOrders([FromQuery] DateTime date, CancellationToken cancellationToken)
    {
        var file = await _exportService.ExportOrdersToExcelAsync(date, cancellationToken);
        return File(file.Content, "text/csv", "orders.csv");
    }

    // Detailed operational export (delivery schedules).
    [HttpGet("delivery")]
    [HttpGet("detailed/delivery")]
    [Produces("text/csv")]
    public async Task<IActionResult> ExportDelivery([FromQuery] DateTime date, CancellationToken cancellationToken)
    {
        var file = await _exportService.ExportDeliveryScheduleAsync(date, cancellationToken);
        return File(file.Content, "text/csv", "delivery.csv");
    }

    // Detailed operational export (Pastel invoice rows).
    [HttpGet("pastel")]
    [HttpGet("detailed/pastel")]
    [Produces("text/csv")]
    public async Task<IActionResult> ExportPastel([FromQuery] DateTime date, CancellationToken cancellationToken)
    {
        var file = await _pastelExportService.GenerateInvoiceFileAsync(date, cancellationToken);
        return File(file.Content, "text/csv", "pastel.csv");
    }
}
