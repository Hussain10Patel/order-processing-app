using Microsoft.AspNetCore.Mvc;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Services;

namespace OrderProcessingApp.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly IOrderService _orderService;
    private readonly IPendingCsvImportService _pendingCsvImportService;
    private readonly IPricingService _pricingService;
    private readonly ILogger<OrdersController> _logger;

    public OrdersController(IOrderService orderService, IPendingCsvImportService pendingCsvImportService, IPricingService pricingService, ILogger<OrdersController> logger)
    {
        _orderService = orderService;
        _pendingCsvImportService = pendingCsvImportService;
        _pricingService = pricingService;
        _logger = logger;
    }

    [HttpPost("manual")]
    public async Task<ActionResult<OrderDto>> CreateManualOrder([FromBody] ManualOrderCreateDto dto, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Manual order payload received. OrderNumber={OrderNumber}, DistributionCentreId={DistributionCentreId}, ItemCount={ItemCount}", dto.OrderNumber, dto.DistributionCentreId, dto.Items.Count);
        try
        {
            var order = await _orderService.CreateManualOrderAsync(dto, cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = order.Id }, order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("create-missing-distribution-centres")]
    public async Task<ActionResult<CreateMissingDistributionCentresResultDto>> CreateMissingDistributionCentres(
        [FromBody] CreateMissingDistributionCentresRequestDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _orderService.CreateMissingDistributionCentresAsync(dto, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("retry-import")]
    public async Task<ActionResult<CsvUploadResultDto>> RetryImport(
        [FromBody] RetryCsvImportDto dto,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"Retry import started for file {dto.FileId}. CreateMissing={dto.CreateMissing}, CreateMissingProducts={dto.CreateMissingProducts}");

        var pendingImport = await _pendingCsvImportService.GetAsync(dto.FileId, cancellationToken);
        if (pendingImport is null)
        {
            return NotFound(new { message = "Pending import not found or expired." });
        }

        if (dto.CreateMissing && pendingImport.MissingDistributionCentres.Count > 0)
        {
            await _orderService.CreateMissingDistributionCentresAsync(new CreateMissingDistributionCentresRequestDto
            {
                Centres = pendingImport.MissingDistributionCentres,
                DistributionCentreId = dto.DistributionCentreId
            }, cancellationToken);
        }

        var processingResult = await _orderService.CreateOrdersFromCsvRowsAsync(pendingImport.Rows, pendingImport.AllowDuplicates, dto.CreateMissingProducts, cancellationToken);
        var result = processingResult.Result;

        if (processingResult.PendingRows.Count > 0)
        {
            result.FileId = await _pendingCsvImportService.SaveAsync(dto.FileId, processingResult.PendingRows, pendingImport.AllowDuplicates, result.MissingDistributionCentres, result.MissingProducts, cancellationToken);
        }
        else
        {
            await _pendingCsvImportService.RemoveAsync(dto.FileId, cancellationToken);
        }

        return Ok(result);
    }

    [HttpGet]
    public async Task<ActionResult<List<OrderDto>>> GetAll(
        [FromQuery(Name = "status")] string? status,
        [FromQuery(Name = "distributionCentreId")] string? distributionCentreId,
        [FromQuery(Name = "distributionCentre")] string? distributionCentre,
        [FromQuery(Name = "startDate")] DateTime? startDate,
        [FromQuery(Name = "endDate")] DateTime? endDate,
        [FromQuery(Name = "fromDate")] DateTime? fromDate,
        [FromQuery(Name = "toDate")] DateTime? toDate,
        [FromQuery(Name = "orderNumber")] string? orderNumber,
        [FromQuery(Name = "productCode")] string? productCode,
        [FromQuery(Name = "productName")] string? productName,
        [FromQuery(Name = "search")] string? search,
        CancellationToken cancellationToken)
    {
        var filter = new OrderFilterDto
        {
            Status = ParseOptionalIntFilter(status),
            DistributionCentreId = ParseOptionalIntFilter(distributionCentreId ?? distributionCentre),
            StartDate = fromDate ?? startDate,
            EndDate = toDate ?? endDate,
            OrderNumber = NormalizeOptionalText(search ?? orderNumber),
            ProductCode = NormalizeOptionalText(productCode),
            ProductName = NormalizeOptionalText(productName)
        };

        var orders = await _orderService.GetFilteredOrdersAsync(filter, cancellationToken);
        return Ok(orders);
    }

    [HttpGet("pricing")]
    public async Task<ActionResult<SystemPriceLookupDto>> GetSystemPrice(
        [FromQuery] int productId,
        [FromQuery] int distributionCentreId,
        CancellationToken cancellationToken)
    {
        if (productId <= 0 || distributionCentreId <= 0)
        {
            return BadRequest(new { message = "ProductId and distributionCentreId are required." });
        }

        var priceLookup = await _pricingService.GetPriceAsync(productId, distributionCentreId, cancellationToken);
        if (!priceLookup.IsFound || !priceLookup.Price.HasValue)
        {
            return NotFound(new { message = "No price found for the selected product and distribution centre." });
        }

        return Ok(new SystemPriceLookupDto
        {
            ProductId = productId,
            DistributionCentreId = distributionCentreId,
            Price = priceLookup.Price.Value
        });
    }

    private static int? ParseOptionalIntFilter(string? value)
    {
        var normalized = NormalizeOptionalText(value);
        if (normalized is null || string.Equals(normalized, "All", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.TryParse(normalized, out var parsedValue) ? parsedValue : null;
    }

    private static string? NormalizeOptionalText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var trimmedValue = value.Trim();
        return string.IsNullOrEmpty(trimmedValue) ? null : trimmedValue;
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<OrderDto>> GetById(int id, CancellationToken cancellationToken)
    {
        var order = await _orderService.GetOrderByIdAsync(id, cancellationToken);
        if (order is null)
        {
            return NotFound();
        }

        return Ok(order);
    }

    [HttpPost("{id:int}/process")]
    public async Task<ActionResult<OrderDto>> ProcessOrder(int id, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Processing order {id}");
        try
        {
            var order = await _orderService.ProcessOrderAsync(id, cancellationToken);
            if (order is null)
            {
                return NotFound();
            }

            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(new { message = ex.Message });
        }
    }

    [HttpPost("{id:int}/approve")]
    public async Task<ActionResult<OrderDto>> ApproveOrder(int id, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Approving order {id}");
        try
        {
            var order = await _orderService.ApproveOrderAsync(id, cancellationToken);
            if (order is null)
            {
                return NotFound();
            }

            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(new { message = ex.Message });
        }
    }

    [HttpPut("{id:int}/adjust")]
    public async Task<ActionResult<OrderDto>> AdjustOrder(int id, [FromBody] AdjustOrderDto dto, CancellationToken cancellationToken)
    {
        if (dto?.Items == null || !dto.Items.Any())
        {
            return BadRequest(new { message = "No items provided." });
        }

        Console.WriteLine($"[AdjustOrder] OrderId={id}, Items received: {dto.Items.Count}");
        foreach (var item in dto.Items)
        {
            Console.WriteLine($"[AdjustOrder] Item: ProductId={item.ProductId}, Qty={item.Quantity}, Price={item.Price}");
        }

        try
        {
            var order = await _orderService.AdjustOrderAsync(id, dto, cancellationToken);
            if (order is null)
            {
                return NotFound();
            }

            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(new { message = ex.Message });
        }
    }

    [HttpPost("{orderId:int}/recalculate")]
    public async Task<ActionResult<OrderDto>> RecalculateOrder(int orderId, CancellationToken cancellationToken)
    {
        var order = await _orderService.RecalculateOrderAsync(orderId, cancellationToken);
        if (order is null)
        {
            return NotFound();
        }

        return Ok(order);
    }
}
