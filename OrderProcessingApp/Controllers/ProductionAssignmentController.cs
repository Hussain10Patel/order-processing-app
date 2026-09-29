using Microsoft.AspNetCore.Mvc;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Services;

namespace OrderProcessingApp.Controllers;

[ApiController]
[Route("api/production-assignment")]
public sealed class ProductionAssignmentController : ControllerBase
{
    private readonly IProductionAssignmentService _assignmentService;

    public ProductionAssignmentController(IProductionAssignmentService assignmentService)
    {
        _assignmentService = assignmentService;
    }

    [HttpGet("orders")]
    public async Task<ActionResult<List<ProductionAssignmentOrderDto>>> GetApprovedOrders(
        [FromQuery] string? assignment,
        [FromQuery] List<int>? distributionCentreIds,
        [FromQuery] string? orderNumber,
        [FromQuery] DateTime? orderDateFrom,
        [FromQuery] DateTime? orderDateTo,
        [FromQuery] DateTime? deliveryDateFrom,
        [FromQuery] DateTime? deliveryDateTo,
        CancellationToken cancellationToken)
    {
        if (!IsValidDateRange(orderDateFrom, orderDateTo) || !IsValidDateRange(deliveryDateFrom, deliveryDateTo))
        {
            return BadRequest(new { message = "From date must be on or before To date." });
        }

        if (!string.IsNullOrWhiteSpace(assignment)
            && !new[] { "all", "assigned", "unassigned" }.Contains(assignment.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "Assignment must be all, assigned, or unassigned." });
        }

        var result = await _assignmentService.GetApprovedOrdersAsync(
            assignment,
            distributionCentreIds,
            orderNumber,
            orderDateFrom,
            orderDateTo,
            deliveryDateFrom,
            deliveryDateTo,
            cancellationToken);

        return Ok(result);
    }

    [HttpPut("orders/{orderId:int}")]
    public async Task<ActionResult<ProductionAssignmentOrderDto>> SetDeliveryDate(
        int orderId,
        [FromBody] SetOrderDeliveryDateDto dto,
        CancellationToken cancellationToken)
    {
        if (dto is null || !dto.HasDeliveryDate)
        {
            return BadRequest(new { message = "The deliveryDate field is required. Use null to clear it." });
        }

        try
        {
            var result = await _assignmentService.SetDeliveryDateAsync(orderId, dto.DeliveryDate, cancellationToken);
            return result is null ? NotFound() : Ok(result);
        }
        catch (InvalidOperationException exception)
        {
            return UnprocessableEntity(new { message = exception.Message });
        }
    }

    private static bool IsValidDateRange(DateTime? from, DateTime? to)
    {
        return !from.HasValue || !to.HasValue || from.Value.Date <= to.Value.Date;
    }
}