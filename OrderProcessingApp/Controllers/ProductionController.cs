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

    [HttpGet]
    public async Task<ActionResult<ProductionResponseDto>> GetByDate([FromQuery] DateTime date, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[DATE FLOW] Controller incoming date: {date:O}, Kind={date.Kind}");
        var result = await _productionService.GetProductionByDateAsync(date, cancellationToken);
        return Ok(result);
    }

    [HttpGet("plans")]
    public async Task<ActionResult<List<ProductionPlanDto>>> GetPlansByDate([FromQuery] DateTime date, CancellationToken cancellationToken)
    {
        var plans = await _productionService.GetPlansByDateAsync(date, cancellationToken);
        return Ok(plans);
    }

    [HttpGet("check")]
    public async Task<ActionResult<List<StockCheckDto>>> CheckStock([FromQuery] DateTime date, CancellationToken cancellationToken)
    {
        var result = await _productionService.CheckStockAsync(date, cancellationToken);
        return Ok(result);
    }
}
