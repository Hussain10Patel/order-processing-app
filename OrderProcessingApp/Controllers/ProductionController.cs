using Microsoft.AspNetCore.Mvc;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Services;

namespace OrderProcessingApp.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductionController : ControllerBase
{
    private readonly IProductionService _productionService;

    public ProductionController(IProductionService productionService)
    {
        _productionService = productionService;
    }

    [HttpPost]
    public async Task<IActionResult> CreateProduction([FromBody] ProductionRequestDto request, CancellationToken cancellationToken)
    {
        if (request.OrderIds == null || !request.OrderIds.Any())
            return BadRequest(new { message = "OrderIds are required." });

        try
        {
            var result = await _productionService.CreateAsync(request.OrderIds, cancellationToken);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpPut]
    public async Task<IActionResult> CreateOrUpdate([FromBody] ProductionPlanUpsertDto dto, CancellationToken cancellationToken)
    {
        try
        {
            await _productionService.CreateOrUpdatePlanAsync(dto, cancellationToken);
            return Ok(new { message = "Production plan saved successfully." });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(new { message = ex.Message });
        }
    }

    [HttpPost("decisions")]
    public async Task<ActionResult<ProductionDecisionResultDto>> SaveDecisions([FromBody] SaveProductionDecisionsDto dto, CancellationToken cancellationToken)
    {
        if (dto.Decisions == null || dto.Decisions.Count == 0)
        {
            return BadRequest(new { message = "At least one production decision is required." });
        }

        try
        {
            var result = await _productionService.SaveProductionDecisionsAsync(dto, cancellationToken);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(new
            {
                errorCode = "PRODUCTION_DECISION_EDIT_BLOCKED",
                message = ex.Message
            });
        }
    }

    [HttpPost("decision")]
    public Task<ActionResult<ProductionDecisionResultDto>> SaveDecision([FromBody] SaveProductionDecisionsDto dto, CancellationToken cancellationToken)
    {
        return SaveDecisions(dto, cancellationToken);
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateTime? date, CancellationToken cancellationToken)
    {
        if (date.HasValue)
        {
            Console.WriteLine($"[DATE FLOW] Controller incoming date: {date:O}, Kind={date.Value.Kind}");
        }

        var result = await _productionService.GetProductionAsync(date, cancellationToken);
        var orders = result.Orders;

        Console.WriteLine($"[PRODUCTION] Returning {orders.Count} visible order(s) to client.");
        foreach (var o in orders)
        {
            Console.WriteLine($"[PRODUCTION][RESPONSE] Id={o.OrderId}, Number={o.OrderNumber}, Status={o.Status}");
        }

        return Ok(new { orders });
    }

    [HttpGet("plans")]
    public async Task<ActionResult<List<ProductionPlanDto>>> GetPlansByDate([FromQuery] DateTime date, CancellationToken cancellationToken)
    {
        var plans = await _productionService.GetPlansByDateAsync(date, cancellationToken);
        return Ok(plans);
    }

    [HttpGet("calendar")]
    public async Task<ActionResult<List<ProductionCalendarDayDto>>> GetCalendar(
        [FromQuery] DateTime fromDate,
        [FromQuery] DateTime toDate,
        CancellationToken cancellationToken)
    {
        if (fromDate > toDate)
        {
            return BadRequest(new { message = "fromDate must be on or before toDate." });
        }

        var result = await _productionService.GetCalendarAsync(fromDate, toDate, cancellationToken);
        return Ok(result);
    }

    [HttpGet("check")]
    public async Task<ActionResult<List<StockCheckDto>>> CheckStock([FromQuery] DateTime date, CancellationToken cancellationToken)
    {
        var result = await _productionService.CheckStockAsync(date, cancellationToken);
        return Ok(result);
    }
}
