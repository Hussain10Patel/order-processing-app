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

    public DeliveryService(AppDbContext dbContext, IAuditService auditService)
    {
        _dbContext = dbContext;
        _auditService = auditService;
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

        if (order.Status == OrderStatus.Flagged)
        {
            throw new InvalidOperationException("Flagged orders must be approved before scheduling");
        }

        var existing = await _dbContext.DeliverySchedules
            .FirstOrDefaultAsync(x => x.OrderId == orderId, cancellationToken);

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

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new DeliveryScheduleDto
        {
            Id = existing.Id,
            OrderId = order.Id,
            OrderNumber = order.OrderNumber,
            DistributionCentre = order.DistributionCentre?.Name ?? string.Empty,
            DeliveryDate = existing.DeliveryDate.ToString("yyyy-MM-dd"),
            Status = existing.Status,
            Notes = existing.Notes,
            TotalPallets = order.TotalPallets > 0 ? order.TotalPallets : order.Items.Sum(x => x.Pallets)
        };
    }

    public async Task<List<DeliveryScheduleDto>> GetScheduleByDateAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeDate(date);
        var start = normalized.Date;
        var end = start.AddDays(1);
        Console.WriteLine($"[FETCH] Incoming date: {date:O}");
        Console.WriteLine($"[FETCH] Normalized date: {normalized:O}");
        Console.WriteLine($"Filtering deliveries by date: {normalized:yyyy-MM-dd}");

        var schedules = await _dbContext.DeliverySchedules
            .AsNoTracking()
            .Include(x => x.Order)
                .ThenInclude(x => x!.DistributionCentre)
            .Include(x => x.Order)
                .ThenInclude(x => x!.Items)
            .Where(x => x.DeliveryDate >= start && x.DeliveryDate < end)
            .OrderBy(x => x.Order!.DistributionCentre!.Name)
            .ThenBy(x => x.Order!.OrderNumber)
            .ToListAsync(cancellationToken);

        foreach (var schedule in schedules)
        {
            Console.WriteLine($"[MATCH] Found order {schedule.OrderId} with date {schedule.DeliveryDate:O}");
        }

        return schedules.Select(x => new DeliveryScheduleDto
        {
            Id = x.Id,
            OrderId = x.OrderId,
            OrderNumber = x.Order?.OrderNumber ?? string.Empty,
            DistributionCentre = x.Order?.DistributionCentre?.Name ?? string.Empty,
            DeliveryDate = x.DeliveryDate.ToString("yyyy-MM-dd"),
            Status = x.Status,
            Notes = x.Notes,
            TotalPallets = x.Order?.Items.Sum(i => i.Pallets) ?? 0
        }).ToList();
    }

    public async Task<List<OrderDto>> GetUnscheduledOrdersByDateAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeDate(date);
        var start = normalized.Date;
        var end = start.AddDays(1);
        Console.WriteLine($"[FETCH][UNSCHEDULED] Incoming date: {date:O}");
        Console.WriteLine($"[FETCH][UNSCHEDULED] Normalized date: {normalized:O}");
        Console.WriteLine($"Fetching unscheduled orders for {normalized:yyyy-MM-dd}");
        Console.WriteLine("[FETCH][UNSCHEDULED] Source field: Order.DeliveryDate; scheduled exclusion field: DeliverySchedule.DeliveryDate");

        var scheduledOrderIds = _dbContext.DeliverySchedules
            .AsNoTracking()
            .Where(x => x.DeliveryDate >= start && x.DeliveryDate < end)
            .Select(x => x.OrderId);

        var unscheduledOrders = await _dbContext.Orders
            .AsNoTracking()
            .Include(x => x.DistributionCentre)
            .Include(x => x.Items)
                .ThenInclude(x => x.Product)
            .Where(x => x.DeliveryDate >= start && x.DeliveryDate < end && !scheduledOrderIds.Contains(x.Id))
            .OrderBy(x => x.DistributionCentre!.Name)
            .ThenBy(x => x.OrderNumber)
            .ToListAsync(cancellationToken);

        foreach (var order in unscheduledOrders)
        {
            Console.WriteLine($"[MATCH] Found order {order.Id} with date {order.DeliveryDate:O}");
        }

        return unscheduledOrders.Select(MapOrderToDto).ToList();
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
                ProductCode = x.ProductCode ?? string.Empty,
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
