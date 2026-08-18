using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;
using System.Globalization;

namespace OrderProcessingApp.Services;

public class DeliveryService : IDeliveryService
{
    private readonly AppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly ILogger<DeliveryService> _logger;

    public DeliveryService(AppDbContext dbContext, IAuditService auditService, ILogger<DeliveryService> logger)
    {
        _dbContext = dbContext;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<DeliveryScheduleDto> ScheduleDeliveryAsync(int orderId, DateTime deliveryDate, string? notes, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeDate(deliveryDate);
        Console.WriteLine($"Scheduling order {orderId} for {normalized:yyyy-MM-dd}");
        Console.WriteLine($"[SCHEDULE] Incoming date: {deliveryDate:O}");
        Console.WriteLine($"[SCHEDULE] Normalized date: {normalized:O}");

        var order = await _dbContext.Orders
            .Include(x => x.Items)
            .Include(x => x.DistributionCentre)
            .FirstOrDefaultAsync(x => x.Id == orderId, cancellationToken);

        if (order is null)
        {
            throw new KeyNotFoundException($"Order not found. OrderId={orderId}.");
        }

        if (!OrderWorkflowStatusRules.IsDeliveryEligible(order.Status))
        {
            throw new InvalidOperationException($"Delivery scheduling is only allowed for {OrderWorkflowStatusRules.DeliveryEligibleStatusLabel} orders. Current status: {order.Status}.");
        }

        _logger.LogInformation("[DELIVERY STATUS FILTER] AllowedStatuses={Statuses}, OrderId={OrderId}, CurrentStatus={Status}", OrderWorkflowStatusRules.DeliveryEligibleStatusLabel, order.Id, order.Status);

        Console.WriteLine($"[DELIVERY] Scheduling allowed. OrderId={order.Id}, OrderNumber={order.OrderNumber}, Status={order.Status}");

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var existing = await _dbContext.DeliverySchedules
            .FirstOrDefaultAsync(x => x.OrderId == orderId, cancellationToken);

        var scheduleAuditOldValue = existing is null ? "Unscheduled" : existing.Status;

        if (existing is null)
        {
            existing = new DeliverySchedule
            {
                OrderId = orderId,
                DeliveryDate = normalized,
                Status = "Scheduled",
                Notes = notes
            };

            _dbContext.DeliverySchedules.Add(existing);
        }
        else
        {
            if (existing.DeliveryDate != normalized)
            {
                _auditService.TrackChange(
                    "Delivery",
                    existing.Id,
                    "DeliveryDate",
                    existing.DeliveryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    normalized.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }

            if (!string.Equals(existing.Notes, notes, StringComparison.Ordinal))
            {
                _auditService.TrackChange("Delivery", existing.Id, "Notes", existing.Notes, notes);
            }

            if (!string.Equals(existing.Status, "Scheduled", StringComparison.Ordinal))
            {
                _auditService.TrackChange("Delivery", existing.Id, "Status", existing.Status, "Scheduled");
            }

            existing.DeliveryDate = normalized;
            existing.Status = "Scheduled";
            existing.Notes = notes;
        }

        var scheduleAuditNewValue = $"Scheduled for {normalized:yyyy-MM-dd}";
        if (existing is not null && (existing.DeliveryDate != normalized || !string.Equals(existing.Status, "Scheduled", StringComparison.Ordinal)))
        {
            _auditService.TrackChange("Delivery", orderId, "Schedule", scheduleAuditOldValue, scheduleAuditNewValue);
        }
        else if (existing is not null)
        {
            _auditService.TrackChange("Delivery", orderId, "Schedule", scheduleAuditOldValue, scheduleAuditNewValue);
        }

        if (order.DeliveryDate != normalized)
        {
            _auditService.TrackChange(
                "Order",
                order.Id,
                "DeliveryDate",
                order.DeliveryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                normalized.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            order.DeliveryDate = normalized;
        }

        Console.WriteLine($"[SCHEDULE] Saved date: {existing.DeliveryDate:O}");
        Console.WriteLine($"[SCHEDULE] Kind: {existing.DeliveryDate.Kind}");
        Console.WriteLine($"[ScheduleDeliveryAsync] Persisting DeliverySchedule.DeliveryDate={existing.DeliveryDate:yyyy-MM-dd} and Order.DeliveryDate={order.DeliveryDate:yyyy-MM-dd} for OrderId={orderId}");
        Console.WriteLine($"[DELIVERY] Order status unchanged after scheduling. OrderId={order.Id}, Status={order.Status}");

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new DeliveryScheduleDto
        {
            Id = existing.Id,
            OrderId = order.Id,
            DistributionCentreId = order.DistributionCentreId,
            OrderNumber = order.OrderNumber,
            DistributionCentre = order.DistributionCentre?.Name ?? string.Empty,
            DeliveryDate = existing.DeliveryDate.ToString("yyyy-MM-dd"),
            Status = existing.Status,
            OrderStatus = order.Status.ToString(),
            IsOrderProcessed = order.Status == OrderStatus.Processed,
            Notes = existing.Notes,
            TotalPallets = order.TotalPallets > 0 ? order.TotalPallets : order.Items.Sum(x => x.Pallets)
        };
    }

    public async Task<bool> UnscheduleDeliveryAsync(int orderId, CancellationToken cancellationToken = default)
    {
        var orderExists = await _dbContext.Orders
            .AsNoTracking()
            .AnyAsync(x => x.Id == orderId, cancellationToken);

        if (!orderExists)
        {
            throw new KeyNotFoundException($"Order not found. OrderId={orderId}.");
        }

        var existing = await _dbContext.DeliverySchedules
            .FirstOrDefaultAsync(x => x.OrderId == orderId, cancellationToken);

        if (existing is null)
        {
            _logger.LogInformation("[DELIVERY UNSCHEDULE] OrderId={OrderId} already unscheduled.", orderId);
            return false;
        }

        _dbContext.DeliverySchedules.Remove(existing);

        _auditService.TrackChange(
            "Delivery",
            orderId,
            "Schedule",
            $"Scheduled for {existing.DeliveryDate:yyyy-MM-dd}",
            "Unscheduled");

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("[DELIVERY UNSCHEDULE] Removed DeliveryScheduleId={DeliveryScheduleId} for OrderId={OrderId}.", existing.Id, orderId);
        return true;
    }

    public async Task<List<DeliveryScheduleDto>> GetScheduleByDateAsync(DateTime? date, CancellationToken cancellationToken = default)
    {
        var normalized = date.HasValue ? NormalizeDate(date.Value) : (DateTime?)null;
        _logger.LogInformation("[DELIVERY LOAD START] Endpoint: GetScheduleByDate, Date: {Date}", normalized?.ToString("yyyy-MM-dd") ?? "(null)");
        Console.WriteLine($"[FETCH] Incoming date: {(date.HasValue ? date.Value.ToString("O") : "(null)")}");
        Console.WriteLine($"[FETCH] Normalized date: {(normalized.HasValue ? normalized.Value.ToString("O") : "(null)")}");
        Console.WriteLine("Fetching all scheduled deliveries (date parameter is treated as display metadata only).");

        try
        {
            var query = _dbContext.DeliverySchedules
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Include(x => x.Order)
                    .ThenInclude(x => x!.DistributionCentre)
                .Include(x => x.Order)
                    .ThenInclude(x => x!.Items)
                        .ThenInclude(x => x.Product)
                .Where(x => x.Order != null && x.Order.IsActive);

            var schedules = await query
                .OrderBy(x => x.Order!.DistributionCentre!.Name)
                .ThenBy(x => x.Order!.OrderNumber)
                .ToListAsync(cancellationToken);

            _logger.LogInformation("[DELIVERY LOAD ALL] Endpoint=GetScheduleByDate, Classification=HasDeliveryScheduleRow, ReturnedSchedules={Count}", schedules.Count);

            var output = new List<DeliveryScheduleDto>();
            foreach (var schedule in schedules)
            {
                try
                {
                    var order = schedule.Order;
                    _logger.LogInformation(
                        "[DELIVERY ORDER] Endpoint: GetScheduleByDate, OrderId: {OrderId}, OrderNumber: {OrderNumber}, DistributionCentreId: {DistributionCentreId}, DistributionCentreName: {DistributionCentreName}",
                        order?.Id,
                        order?.OrderNumber ?? string.Empty,
                        order?.DistributionCentreId,
                        order?.DistributionCentre?.Name ?? string.Empty);

                    if (order is null)
                    {
                        _logger.LogWarning("[DELIVERY ORDER] Endpoint: GetScheduleByDate, Malformed schedule detected. ScheduleId: {ScheduleId}, Reason: Order is null", schedule.Id);
                        continue;
                    }

                    foreach (var item in order.Items)
                    {
                        _logger.LogInformation(
                            "[DELIVERY ITEM] Endpoint: GetScheduleByDate, OrderId: {OrderId}, OrderItemId: {OrderItemId}, ProductId: {ProductId}, ProductName: {ProductName}, SKUCode: {SKUCode}",
                            order.Id,
                            item.Id,
                            item.ProductId,
                            item.Product?.Name ?? item.ProductName ?? string.Empty,
                            item.Product?.SKUCode ?? string.Empty);
                    }

                    output.Add(new DeliveryScheduleDto
                    {
                        Id = schedule.Id,
                        OrderId = schedule.OrderId,
                        DistributionCentreId = order.DistributionCentreId,
                        OrderNumber = order.OrderNumber ?? string.Empty,
                        DistributionCentre = order.DistributionCentre?.Name ?? string.Empty,
                        DeliveryDate = schedule.DeliveryDate.ToString("yyyy-MM-dd"),
                        Status = schedule.Status,
                        OrderStatus = order.Status.ToString(),
                        IsOrderProcessed = order.Status == OrderStatus.Processed,
                        Notes = schedule.Notes,
                        TotalPallets = order.Items.Sum(i => i.Pallets)
                    });
                }
                catch (Exception itemException)
                {
                    _logger.LogError(itemException, "[DELIVERY ERROR] Endpoint: GetScheduleByDate, ScheduleId: {ScheduleId}", schedule.Id);
                }
            }

            return output;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "[DELIVERY ERROR] Endpoint: GetScheduleByDate, Date: {Date}", normalized?.ToString("yyyy-MM-dd") ?? "(null)");
            throw;
        }
    }

    public async Task<List<OrderDto>> GetUnscheduledOrdersByDateAsync(DateTime? date, CancellationToken cancellationToken = default)
    {
        var normalized = date.HasValue ? NormalizeDate(date.Value) : (DateTime?)null;
        _logger.LogInformation("[DELIVERY LOAD START] Endpoint: GetUnscheduledOrdersByDate, Date: {Date}", normalized?.ToString("yyyy-MM-dd") ?? "(null)");
        Console.WriteLine($"[FETCH][UNSCHEDULED] Incoming date: {(date.HasValue ? date.Value.ToString("O") : "(null)")}");
        Console.WriteLine($"[FETCH][UNSCHEDULED] Normalized date: {(normalized.HasValue ? normalized.Value.ToString("O") : "(null)")}");
        Console.WriteLine("Fetching all unscheduled eligible orders (date parameter is treated as display metadata only).");
        Console.WriteLine("[FETCH][UNSCHEDULED] Classification field: DeliverySchedule.OrderId absence");

        var scheduledOrderIds = _dbContext.DeliverySchedules
            .AsNoTracking()
            .Select(x => x.OrderId);

        try
        {
            var eligibleOrders = _dbContext.Orders
                .AsNoTracking()
                .Include(x => x.DistributionCentre)
                .Include(x => x.Items)
                    .ThenInclude(x => x.Product)
                .Where(x => OrderWorkflowStatusRules.DeliveryEligibleStatuses.Contains(x.Status)
                    && !scheduledOrderIds.Contains(x.Id));

            var unscheduledOrders = await eligibleOrders
                .OrderBy(x => x.DistributionCentre!.Name)
                .ThenBy(x => x.OrderNumber)
                .ToListAsync(cancellationToken);

            _logger.LogInformation("[DELIVERY LOAD ALL] Endpoint=GetUnscheduledOrdersByDate, AllowedStatuses={Statuses}, Classification=NoDeliveryScheduleRow, ReturnedOrders={Count}", OrderWorkflowStatusRules.DeliveryEligibleStatusLabel, unscheduledOrders.Count);

            var output = new List<OrderDto>();
            foreach (var order in unscheduledOrders)
            {
                try
                {
                    _logger.LogInformation(
                        "[DELIVERY ORDER] Endpoint: GetUnscheduledOrdersByDate, OrderId: {OrderId}, OrderNumber: {OrderNumber}, DistributionCentreId: {DistributionCentreId}, DistributionCentreName: {DistributionCentreName}",
                        order.Id,
                        order.OrderNumber,
                        order.DistributionCentreId,
                        order.DistributionCentre?.Name ?? string.Empty);

                    foreach (var item in order.Items)
                    {
                        _logger.LogInformation(
                            "[DELIVERY ITEM] Endpoint: GetUnscheduledOrdersByDate, OrderId: {OrderId}, OrderItemId: {OrderItemId}, ProductId: {ProductId}, ProductName: {ProductName}, SKUCode: {SKUCode}",
                            order.Id,
                            item.Id,
                            item.ProductId,
                            item.Product?.Name ?? item.ProductName ?? string.Empty,
                            item.Product?.SKUCode ?? string.Empty);
                    }

                    output.Add(MapOrderToDto(order));
                }
                catch (Exception itemException)
                {
                    _logger.LogError(itemException, "[DELIVERY ERROR] Endpoint: GetUnscheduledOrdersByDate, OrderId: {OrderId}", order.Id);
                }
            }

            return output;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "[DELIVERY ERROR] Endpoint: GetUnscheduledOrdersByDate, Date: {Date}", normalized?.ToString("yyyy-MM-dd") ?? "(null)");
            throw;
        }
    }

    private static DateTime NormalizeDate(DateTime date)
    {
        return DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
    }

    private static OrderDto MapOrderToDto(Order order)
    {
        var hasMissing = order.Items.Any(x => x.IsPriceMissing);
        var hasMismatch = order.Items.Any(x => x.IsPriceMismatch);

        return new OrderDto
        {
            Id = order.Id,
            OrderNumber = order.OrderNumber,
            OrderDate = order.OrderDate.ToString("yyyy-MM-dd"),
            DeliveryDate = order.DeliveryDate.ToString("yyyy-MM-dd"),
            DistributionCentreId = order.DistributionCentreId,
            DistributionCentreName = order.DistributionCentre?.Name ?? string.Empty,
            Source = order.Source,
            Status = order.Status,
            StatusLabel = order.Status.ToString(),
            Notes = order.Notes,
            IsPriceMissing = hasMissing,
            IsPriceMismatch = hasMismatch,
            IsAdjusted = order.IsAdjusted,
            TotalValue = order.TotalValue,
            TotalPallets = order.TotalPallets,
            Items = order.Items.Select(x => new OrderItemDto
            {
                Id = x.Id,
                ProductId = x.ProductId,
                ProductName = x.ProductName ?? x.Product?.Name ?? string.Empty,
                ProductCode = x.ProductCode ?? x.Product?.SKUCode ?? string.Empty,
                SKUCode = x.Product?.SKUCode ?? string.Empty,
                Quantity = x.Quantity,
                Price = x.Price,
                Pallets = x.Pallets,
                LineTotal = x.Quantity * x.Price,
                IsUnmapped = x.IsUnmapped,
                IsPriceMissing = x.IsPriceMissing,
                IsPriceMismatch = x.IsPriceMismatch,
                IsCsvPrice = x.IsCsvPrice
            }).ToList()
        };
    }
}
