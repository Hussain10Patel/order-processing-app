using Microsoft.AspNetCore.Mvc;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Services;

namespace OrderProcessingApp.Controllers;

[ApiController]
[Route("api/reports")]
public class ReportsController : ControllerBase
{
    private readonly IReportService _reportService;

    public ReportsController(IReportService reportService)
    {
        _reportService = reportService;
    }

    [HttpGet]
    public async Task<ActionResult<object>> Get(CancellationToken cancellationToken)
    {
        var orders = await _reportService.GetOrdersReportAsync(cancellationToken);
        var sales = await _reportService.GetSalesSummaryAsync(cancellationToken);

        return Ok(new
        {
            orders,
            sales
        });
    }

    [HttpGet("summary")]
    public async Task<ActionResult<ReportSummaryDto>> Summary([FromQuery] DateTime date, CancellationToken cancellationToken)
    {
        var result = await _reportService.GetSummaryByDeliveryDateAsync(date, cancellationToken);
        return Ok(result);
    }

    [HttpGet("supplier-summary")]
    public async Task<ActionResult<List<SupplierSummaryGroupDto>>> SupplierSummary([FromQuery] DateTime date, CancellationToken cancellationToken)
    {
        var result = await _reportService.GetSupplierSummaryAsync(date, cancellationToken);
        return Ok(result);
    }

    [HttpGet("supplier-delivery")]
    public async Task<ActionResult<List<SupplierSummaryItemDto>>> SupplierDelivery([FromQuery] DateTime? date, CancellationToken cancellationToken)
    {
        var result = await _reportService.GetSupplierDeliveryAsync(date, cancellationToken);
        return Ok(result);
    }

    [HttpGet("daily-delivery")]
    public async Task<ActionResult<List<DailyDeliveryGroupDto>>> DailyDelivery([FromQuery] DateTime date, CancellationToken cancellationToken)
    {
        var result = await _reportService.GetDailyDeliveryReportAsync(date, cancellationToken);
        return Ok(result);
    }

    [HttpGet("orders")]
    public async Task<ActionResult<OrdersReportDto>> OrdersReport(CancellationToken cancellationToken)
    {
        var result = await _reportService.GetOrdersReportAsync(cancellationToken);
        return Ok(result);
    }

    [HttpGet("sales")]
    public async Task<ActionResult<SalesReportDto>> SalesReport(CancellationToken cancellationToken)
    {
        var result = await _reportService.GetSalesSummaryAsync(cancellationToken);
        return Ok(result);
    }
}
