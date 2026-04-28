using Microsoft.AspNetCore.Mvc;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Services;

namespace OrderProcessingApp.Controllers;

[ApiController]
[Route("api/audit")]
public class AuditController : ControllerBase
{
    private readonly IAuditService _auditService;

    public AuditController(IAuditService auditService)
    {
        _auditService = auditService;
    }

    [HttpGet]
    public async Task<ActionResult<List<AuditLogDto>>> GetAll(CancellationToken cancellationToken)
    {
        var result = await _auditService.GetAllAsync(cancellationToken);
        return Ok(result);
    }

    [HttpGet("order/{id:int}")]
    public async Task<ActionResult<List<AuditLogDto>>> GetByOrder(int id, CancellationToken cancellationToken)
    {
        var result = await _auditService.GetByEntityAsync("Order", id, cancellationToken);
        return Ok(result);
    }
}
