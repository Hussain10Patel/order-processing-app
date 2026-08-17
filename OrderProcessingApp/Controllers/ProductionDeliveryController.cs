using Microsoft.AspNetCore.Mvc;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Services;

namespace OrderProcessingApp.Controllers;

[ApiController]
[Route("api/production-delivery")]
public class ProductionDeliveryController : ControllerBase
{
    private readonly IProductionDeliveryPlannerService _plannerService;

    public ProductionDeliveryController(IProductionDeliveryPlannerService plannerService)
    {
        _plannerService = plannerService;
    }

    [HttpGet]
    public async Task<ActionResult<ProductionDeliveryPlanDto>> GetCurrent(CancellationToken cancellationToken)
    {
        var result = await _plannerService.GetCurrentPlanAsync(cancellationToken);
        return Ok(result);
    }

    [HttpPut("opening-stock")]
    public async Task<ActionResult<ProductionDeliveryPlanDto>> UpdateOpeningStock(
        [FromBody] ProductionDeliveryPlanQuantitiesUpdateDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _plannerService.UpdateOpeningStockAsync(dto, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("events/{afterEventId:int}/production")]
    public async Task<ActionResult<ProductionDeliveryPlanDto>> AddProductionEvent(int afterEventId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _plannerService.AddProductionEventAsync(afterEventId, cancellationToken);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpPost("events/{afterEventId:int}/adjustment")]
    public async Task<ActionResult<ProductionDeliveryPlanDto>> AddAdjustmentEvent(int afterEventId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _plannerService.AddStockAdjustmentEventAsync(afterEventId, cancellationToken);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpPut("events/{eventId:int}/quantities")]
    public async Task<ActionResult<ProductionDeliveryPlanDto>> UpdateEventQuantities(
        int eventId,
        [FromBody] ProductionDeliveryPlanQuantitiesUpdateDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _plannerService.UpdateEventQuantitiesAsync(eventId, dto, cancellationToken);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("events/{eventId:int}/delivery-date")]
    public async Task<ActionResult<ProductionDeliveryPlanDto>> UpdateOrderDeliveryDate(
        int eventId,
        [FromBody] ProductionDeliveryPlanDeliveryDateUpdateDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _plannerService.UpdateOrderDeliveryDateAsync(eventId, dto, cancellationToken);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("events/{eventId:int}")]
    public async Task<ActionResult<ProductionDeliveryPlanDto>> DeleteEvent(int eventId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _plannerService.DeleteEventAsync(eventId, cancellationToken);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}