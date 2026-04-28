using Microsoft.AspNetCore.Mvc;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Services;

namespace OrderProcessingApp.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DeliveryController : ControllerBase
{
    private readonly IDeliveryService _deliveryService;
    private readonly ILogger<DeliveryController> _logger;

    public DeliveryController(IDeliveryService deliveryService, ILogger<DeliveryController> logger)
    {
        _deliveryService = deliveryService;
        _logger = logger;
    }

    [HttpPost("schedule")]
    public async Task<ActionResult<DeliveryScheduleDto>> Schedule([FromBody] ScheduleDeliveryDto dto, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Delivery schedule payload received. OrderId={OrderId}, DeliveryDate={DeliveryDate}", dto.OrderId, dto.DeliveryDate);
        Console.WriteLine($"Scheduling delivery for OrderId {dto.OrderId}");
        Console.WriteLine($"[SCHEDULE] Incoming date: {dto.DeliveryDate:O}");
        try
        {
            var result = await _deliveryService.ScheduleDeliveryAsync(dto.OrderId, dto.DeliveryDate, dto.Notes, cancellationToken);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message, orderId = dto.OrderId });
        }
        catch (DeliveryValidationException ex)
        {
            return BadRequest(new
            {
                message = ex.Message,
                orderId = ex.OrderId,
                orderNumber = ex.OrderNumber,
                currentStatus = ex.CurrentStatus.ToString()
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message, orderId = dto.OrderId });
        }
    }

    [HttpGet]
    public async Task<ActionResult<List<DeliveryScheduleDto>>> GetByDate([FromQuery] DateTime date, CancellationToken cancellationToken)
    {
        var result = await _deliveryService.GetScheduleByDateAsync(date, cancellationToken);
        return Ok(result);
    }

    [HttpGet("unscheduled")]
    public async Task<ActionResult<List<OrderDto>>> GetUnscheduledByDate([FromQuery] DateTime date, CancellationToken cancellationToken)
    {
        var result = await _deliveryService.GetUnscheduledOrdersByDateAsync(date, cancellationToken);
        return Ok(result);
    }
}
