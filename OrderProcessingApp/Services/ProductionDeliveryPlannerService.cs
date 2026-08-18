using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;

namespace OrderProcessingApp.Services;

public sealed class ProductionDeliveryPlannerService : IProductionDeliveryPlannerService
{
    private const string DefaultPlanName = "Production / Delivery";

    private readonly AppDbContext _dbContext;
    private readonly IProductionService _productionService;

    public ProductionDeliveryPlannerService(AppDbContext dbContext, IProductionService productionService)
    {
        _dbContext = dbContext;
        _productionService = productionService;
    }

    public async Task<ProductionDeliveryPlanDto> GetCurrentPlanAsync(CancellationToken cancellationToken = default)
    {
        var context = await LoadContextAsync(cancellationToken);
        await EnsureOpeningStockAsync(context.Plan, context.Products, cancellationToken);
        var hadOrphans = await EnsureOrderEventsAsync(context.Plan, context.EligibleOrders, context.SchedulesByOrderId, cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (hadOrphans)
        {
            await RenumberEventsAsync(context.Plan.Id, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return await BuildPlanDtoAsync(context, cancellationToken);
    }

    public async Task<ProductionDeliveryPlanDto> UpdateOpeningStockAsync(ProductionDeliveryPlanQuantitiesUpdateDto dto, CancellationToken cancellationToken = default)
    {
        var context = await LoadContextAsync(cancellationToken);
        var openingEvent = await EnsureOpeningStockAsync(context.Plan, context.Products, cancellationToken);

        await SyncQuantitiesAsync(openingEvent, dto.Quantities, cancellationToken);
        await TouchPlanAsync(context.Plan);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await BuildPlanDtoAsync(context, cancellationToken);
    }

    public async Task<ProductionDeliveryPlanDto> AddProductionEventAsync(int afterEventId, CancellationToken cancellationToken = default)
    {
        return await AddEventAsync(afterEventId, ProductionDeliveryPlanEventType.Production, cancellationToken);
    }

    public async Task<ProductionDeliveryPlanDto> AddStockAdjustmentEventAsync(int afterEventId, CancellationToken cancellationToken = default)
    {
        return await AddEventAsync(afterEventId, ProductionDeliveryPlanEventType.StockAdjustment, cancellationToken);
    }

    public async Task<ProductionDeliveryPlanDto> UpdateEventQuantitiesAsync(int eventId, ProductionDeliveryPlanQuantitiesUpdateDto dto, CancellationToken cancellationToken = default)
    {
        var context = await LoadContextAsync(cancellationToken);
        var plannerEvent = await _dbContext.ProductionDeliveryPlanEvents
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == eventId, cancellationToken);

        if (plannerEvent is null)
        {
            throw new KeyNotFoundException($"Planner event not found. EventId={eventId}.");
        }

        if (plannerEvent.EventType == ProductionDeliveryPlanEventType.Order)
        {
            throw new InvalidOperationException("Order event quantities are driven by the live order data and cannot be edited here.");
        }

        await SyncQuantitiesAsync(plannerEvent, dto.Quantities, cancellationToken);
        await TouchPlanAsync(context.Plan);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await BuildPlanDtoAsync(context, cancellationToken);
    }

    public async Task<ProductionDeliveryPlanDto> UpdateOrderDeliveryDateAsync(int eventId, ProductionDeliveryPlanDeliveryDateUpdateDto dto, CancellationToken cancellationToken = default)
    {
        var context = await LoadContextAsync(cancellationToken);
        var plannerEvent = await _dbContext.ProductionDeliveryPlanEvents
            .FirstOrDefaultAsync(x => x.Id == eventId, cancellationToken);

        if (plannerEvent is null)
        {
            throw new KeyNotFoundException($"Planner event not found. EventId={eventId}.");
        }

        if (plannerEvent.EventType != ProductionDeliveryPlanEventType.Order)
        {
            throw new InvalidOperationException("Only order events can update delivery dates.");
        }

        if (!plannerEvent.OrderId.HasValue)
        {
            throw new InvalidOperationException("Order event is missing an OrderId.");
        }

        if (!dto.DeliveryDate.HasValue)
        {
            throw new InvalidOperationException("Delivery date is required.");
        }

        var order = await _dbContext.Orders
            .FirstOrDefaultAsync(x => x.Id == plannerEvent.OrderId.Value, cancellationToken);

        if (order is null)
        {
            throw new KeyNotFoundException($"Order not found. OrderId={plannerEvent.OrderId.Value}.");
        }

        var normalizedDate = DateTime.SpecifyKind(dto.DeliveryDate.Value.Date, DateTimeKind.Unspecified);

        plannerEvent.PlannedDeliveryDate = normalizedDate;
        plannerEvent.UpdatedAt = Now();
        order.DeliveryDate = normalizedDate;

        await TouchPlanAsync(context.Plan);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await BuildPlanDtoAsync(context, cancellationToken);
    }

    public async Task<ProductionDeliveryPlanDto> DeleteEventAsync(int eventId, CancellationToken cancellationToken = default)
    {
        var context = await LoadContextAsync(cancellationToken);
        var plannerEvent = await _dbContext.ProductionDeliveryPlanEvents
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == eventId, cancellationToken);

        if (plannerEvent is null)
        {
            throw new KeyNotFoundException($"Planner event not found. EventId={eventId}.");
        }

        if (plannerEvent.EventType is ProductionDeliveryPlanEventType.Order or ProductionDeliveryPlanEventType.OpeningStock)
        {
            throw new InvalidOperationException("Order and opening stock events cannot be deleted.");
        }

        _dbContext.ProductionDeliveryPlanEventLines.RemoveRange(plannerEvent.Lines);
        _dbContext.ProductionDeliveryPlanEvents.Remove(plannerEvent);
        await RenumberEventsAsync(context.Plan.Id, cancellationToken);
        await TouchPlanAsync(context.Plan);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await BuildPlanDtoAsync(context, cancellationToken);
    }

    public async Task<ProductionDeliveryPlanDto> RemoveFromPlanAsync(int orderId, CancellationToken cancellationToken = default)
    {
        var context = await LoadContextAsync(cancellationToken);

        var order = await _dbContext.Orders
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == orderId, cancellationToken);

        if (order is null)
        {
            throw new KeyNotFoundException($"Order not found. OrderId={orderId}.");
        }

        // Remove the Order's planner event
        var planEvent = context.Plan.Events
            .FirstOrDefault(x => x.EventType == ProductionDeliveryPlanEventType.Order && x.OrderId == orderId);

        if (planEvent is not null)
        {
            // Remove owned Production and StockAdjustment events first
            var ownedEvents = context.Plan.Events
                .Where(x => x.OwnerOrderId == orderId)
                .ToList();

            foreach (var owned in ownedEvents)
            {
                _dbContext.ProductionDeliveryPlanEventLines.RemoveRange(owned.Lines);
                _dbContext.ProductionDeliveryPlanEvents.Remove(owned);
                context.Plan.Events.Remove(owned);
            }

            _dbContext.ProductionDeliveryPlanEventLines.RemoveRange(planEvent.Lines);
            _dbContext.ProductionDeliveryPlanEvents.Remove(planEvent);
            context.Plan.Events.Remove(planEvent);
        }

        order.IsExcludedFromPlan = true;

        await RenumberEventsAsync(context.Plan.Id, cancellationToken);
        await TouchPlanAsync(context.Plan);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await BuildPlanDtoAsync(context, cancellationToken);
    }

    public async Task<ProductionDeliveryPlanDto> AddToPlanAsync(int orderId, CancellationToken cancellationToken = default)
    {
        var orderEntity = await _dbContext.Orders
            .FirstOrDefaultAsync(x => x.Id == orderId, cancellationToken);

        if (orderEntity is null)
        {
            throw new KeyNotFoundException($"Order not found or inactive. OrderId={orderId}.");
        }

        if (!OrderWorkflowStatusRules.ProductionAndDeliveryQueryableStatuses.Contains(orderEntity.Status))
        {
            throw new InvalidOperationException($"Order is not eligible for the Production/Delivery planner. Status: {orderEntity.Status}.");
        }

        orderEntity.IsExcludedFromPlan = false;
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Delegate to GetCurrentPlanAsync which will auto-add via EnsureOrderEventsAsync
        return await GetCurrentPlanAsync(cancellationToken);
    }

    public async Task<List<OrderExcludedFromPlanDto>> GetExcludedOrdersAsync(CancellationToken cancellationToken = default)
    {
        var excluded = await _dbContext.Orders
            .AsNoTracking()
            .Include(x => x.DistributionCentre)
            .Where(x => x.IsExcludedFromPlan
                     && OrderWorkflowStatusRules.ProductionAndDeliveryQueryableStatuses.Contains(x.Status))
            .OrderBy(x => x.DeliveryDate)
            .ThenBy(x => x.OrderNumber)
            .ToListAsync(cancellationToken);

        return excluded.Select(o => new OrderExcludedFromPlanDto
        {
            Id = o.Id,
            OrderNumber = o.OrderNumber,
            DistributionCentre = o.DistributionCentre?.Name ?? string.Empty,
            DeliveryDate = o.DeliveryDate.ToString("yyyy-MM-dd"),
            Status = o.Status.ToString()
        }).ToList();
    }

    private async Task<PlannerContext> LoadContextAsync(CancellationToken cancellationToken)
    {
        var productionSnapshot = await _productionService.GetProductionAsync(null, cancellationToken);
        var eligibleOrders = productionSnapshot.Orders
            .OrderBy(x => x.DeliveryDate)
            .ThenBy(x => x.OrderNumber)
            .ToList();

        var products = BuildProductCatalog(eligibleOrders);
        var orderDatesByOrderId = await _dbContext.Orders
            .AsNoTracking()
            .Where(x => eligibleOrders.Select(order => order.OrderId).Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.OrderDate, cancellationToken);
        var schedulesByOrderId = await LoadSchedulesByOrderIdAsync(eligibleOrders.Select(x => x.OrderId).ToList(), cancellationToken);
        var plan = await GetOrCreatePlanAsync(cancellationToken);

        return new PlannerContext(plan, eligibleOrders, products, orderDatesByOrderId, schedulesByOrderId);
    }

    private async Task<ProductionDeliveryPlanDto> AddEventAsync(int afterEventId, ProductionDeliveryPlanEventType eventType, CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(cancellationToken);
        var afterEvent = await _dbContext.ProductionDeliveryPlanEvents.FirstOrDefaultAsync(x => x.Id == afterEventId, cancellationToken);

        if (afterEvent is null)
        {
            throw new KeyNotFoundException($"Planner event not found. EventId={afterEventId}.");
        }

        // Determine ownership: find the nearest Order event at or before afterEvent's sequence
        var ownerOrderEvent = context.Plan.Events
            .Where(x => x.EventType == ProductionDeliveryPlanEventType.Order
                     && x.Sequence <= afterEvent.Sequence)
            .OrderByDescending(x => x.Sequence)
            .FirstOrDefault();
        var ownerOrderId = ownerOrderEvent?.OrderId;

        var newSequence = afterEvent.Sequence + 1;
        await ShiftSequencesAsync(context.Plan.Id, newSequence, cancellationToken);

        var now = Now();
        _dbContext.ProductionDeliveryPlanEvents.Add(new ProductionDeliveryPlanEvent
        {
            PlanId = context.Plan.Id,
            Sequence = newSequence,
            EventType = eventType,
            OwnerOrderId = ownerOrderId,
            CreatedAt = now,
            UpdatedAt = now
        });

        await TouchPlanAsync(context.Plan);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await BuildPlanDtoAsync(context, cancellationToken);
    }

    private async Task<ProductionDeliveryPlan> GetOrCreatePlanAsync(CancellationToken cancellationToken)
    {
        var plan = await _dbContext.ProductionDeliveryPlans
            .Include(x => x.Events)
                .ThenInclude(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Name == DefaultPlanName, cancellationToken);

        if (plan is not null)
        {
            return plan;
        }

        var now = Now();
        plan = new ProductionDeliveryPlan
        {
            Name = DefaultPlanName,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.ProductionDeliveryPlans.Add(plan);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return await _dbContext.ProductionDeliveryPlans
            .Include(x => x.Events)
                .ThenInclude(x => x.Lines)
            .FirstAsync(x => x.Id == plan.Id, cancellationToken);
    }

    private async Task<ProductionDeliveryPlanEvent> EnsureOpeningStockAsync(
        ProductionDeliveryPlan plan,
        IReadOnlyList<ProductionDeliveryPlanProductDto> productCatalog,
        CancellationToken cancellationToken)
    {
        var openingEvent = plan.Events.FirstOrDefault(x => x.EventType == ProductionDeliveryPlanEventType.OpeningStock);
        var now = Now();

        var stockByProductId = await _dbContext.Stocks
            .AsNoTracking()
            .Where(x => productCatalog.Select(p => p.ProductId).Contains(x.ProductId))
            .ToDictionaryAsync(x => x.ProductId, x => x.Quantity, cancellationToken);

        if (openingEvent is null)
        {
            openingEvent = new ProductionDeliveryPlanEvent
            {
                PlanId = plan.Id,
                Sequence = 1,
                EventType = ProductionDeliveryPlanEventType.OpeningStock,
                CreatedAt = now,
                UpdatedAt = now
            };

            plan.Events.Add(openingEvent);
        }

        var existingLineProductIds = openingEvent.Lines.Select(x => x.ProductId).ToHashSet();
        foreach (var product in productCatalog)
        {
            if (existingLineProductIds.Contains(product.ProductId))
            {
                continue;
            }

            openingEvent.Lines.Add(new ProductionDeliveryPlanEventLine
            {
                ProductId = product.ProductId,
                Quantity = stockByProductId.TryGetValue(product.ProductId, out var openingStock) ? openingStock : 0m,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        openingEvent.UpdatedAt = now;
        return openingEvent;
    }

    private async Task<bool> EnsureOrderEventsAsync(
        ProductionDeliveryPlan plan,
        IReadOnlyList<ProductionOrderDto> eligibleOrders,
        IReadOnlyDictionary<int, DeliverySchedule> schedulesByOrderId,
        CancellationToken cancellationToken)
    {
        var now = Now();

        // Load order IDs explicitly excluded from plan
        var excludedIds = (await _dbContext.Orders
            .AsNoTracking()
            .Where(x => x.IsExcludedFromPlan)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        var existingOrderEvents = plan.Events
            .Where(x => x.EventType == ProductionDeliveryPlanEventType.Order && x.OrderId.HasValue)
            .ToDictionary(x => x.OrderId!.Value, x => x);

        // CLEANUP: Remove orphan Order events for orders that are no longer eligible
        // (inactive/soft-deleted, wrong status, or otherwise absent from eligibleOrders)
        var eligibleOrderIds = eligibleOrders.Select(x => x.OrderId).ToHashSet();
        var orphanEvents = plan.Events
            .Where(x => x.EventType == ProductionDeliveryPlanEventType.Order
                     && x.OrderId.HasValue
                     && !eligibleOrderIds.Contains(x.OrderId.Value))
            .ToList();

        foreach (var orphan in orphanEvents)
        {
            // Also remove any Production/StockAdjustment events owned by this orphan order
            var ownedEvents = plan.Events
                .Where(x => x.OwnerOrderId == orphan.OrderId)
                .ToList();

            foreach (var owned in ownedEvents)
            {
                _dbContext.ProductionDeliveryPlanEventLines.RemoveRange(owned.Lines);
                _dbContext.ProductionDeliveryPlanEvents.Remove(owned);
                plan.Events.Remove(owned);
            }

            _dbContext.ProductionDeliveryPlanEventLines.RemoveRange(orphan.Lines);
            _dbContext.ProductionDeliveryPlanEvents.Remove(orphan);
            plan.Events.Remove(orphan);
            existingOrderEvents.Remove(orphan.OrderId!.Value);
        }

        var hadOrphans = orphanEvents.Count > 0;

        var nextSequence = plan.Events.Count == 0 ? 0 : plan.Events.Max(x => x.Sequence);

        foreach (var order in eligibleOrders)
        {
            // Skip orders explicitly excluded from the plan
            if (excludedIds.Contains(order.OrderId))
            {
                continue;
            }

            if (existingOrderEvents.TryGetValue(order.OrderId, out var existingEvent))
            {
                var plannedDate = ResolvePlannedDeliveryDate(order, schedulesByOrderId.ContainsKey(order.OrderId));
                if (existingEvent.PlannedDeliveryDate != plannedDate)
                {
                    existingEvent.PlannedDeliveryDate = plannedDate;
                    existingEvent.UpdatedAt = now;
                }

                continue;
            }

            nextSequence += 1;
            plan.Events.Add(new ProductionDeliveryPlanEvent
            {
                PlanId = plan.Id,
                Sequence = nextSequence,
                EventType = ProductionDeliveryPlanEventType.Order,
                OrderId = order.OrderId,
                PlannedDeliveryDate = ResolvePlannedDeliveryDate(order, schedulesByOrderId.ContainsKey(order.OrderId)),
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        return hadOrphans;
    }

    private async Task SyncQuantitiesAsync(
        ProductionDeliveryPlanEvent plannerEvent,
        IReadOnlyCollection<ProductionDeliveryPlanProductQuantityDto> quantities,
        CancellationToken cancellationToken)
    {
        var now = Now();
        var nextByProductId = quantities.ToDictionary(x => x.ProductId, x => x.Quantity);
        var existingByProductId = plannerEvent.Lines.ToDictionary(x => x.ProductId, x => x);

        foreach (var line in plannerEvent.Lines.ToList())
        {
            if (!nextByProductId.ContainsKey(line.ProductId))
            {
                plannerEvent.Lines.Remove(line);
                _dbContext.ProductionDeliveryPlanEventLines.Remove(line);
            }
        }

        foreach (var quantity in quantities)
        {
            if (existingByProductId.TryGetValue(quantity.ProductId, out var line))
            {
                line.Quantity = quantity.Quantity;
                line.UpdatedAt = now;
                continue;
            }

            plannerEvent.Lines.Add(new ProductionDeliveryPlanEventLine
            {
                ProductId = quantity.ProductId,
                Quantity = quantity.Quantity,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        plannerEvent.UpdatedAt = now;
        await Task.CompletedTask;
    }

    private async Task<ProductionDeliveryPlanDto> BuildPlanDtoAsync(PlannerContext context, CancellationToken cancellationToken)
    {
        var plan = await _dbContext.ProductionDeliveryPlans
            .AsNoTracking()
            .Include(x => x.Events)
                .ThenInclude(x => x.Lines)
                    .ThenInclude(x => x.Product)
            .FirstAsync(x => x.Id == context.Plan.Id, cancellationToken);

        var events = plan.Events
            .OrderBy(x => x.Sequence)
            .ThenBy(x => x.Id)
            .ToList();

        var runningStock = context.Products.ToDictionary(x => x.ProductId, _ => 0m);
        var eventDtos = new List<ProductionDeliveryPlanEventDto>();
        var orderById = context.EligibleOrders.ToDictionary(x => x.OrderId, x => x);

        foreach (var plannerEvent in events)
        {
            var before = CloneStock(runningStock);
            var quantities = await GetEventQuantitiesAsync(plannerEvent, orderById, cancellationToken);
            ApplyEventQuantities(plannerEvent.EventType, runningStock, quantities);
            var after = CloneStock(runningStock);

            var currentOrder = plannerEvent.OrderId.HasValue && orderById.TryGetValue(plannerEvent.OrderId.Value, out var order)
                ? order
                : null;

            var scheduled = plannerEvent.OrderId.HasValue && context.SchedulesByOrderId.ContainsKey(plannerEvent.OrderId.Value);

            eventDtos.Add(new ProductionDeliveryPlanEventDto
            {
                Id = plannerEvent.Id,
                Sequence = plannerEvent.Sequence,
                EventType = plannerEvent.EventType.ToString(),
                OrderId = plannerEvent.OrderId,
                OrderNumber = currentOrder?.OrderNumber,
                DistributionCentreId = currentOrder?.DistributionCentreId,
                DistributionCentreName = currentOrder?.DistributionCentre,
                OrderDate = currentOrder is null || !context.OrderDatesByOrderId.TryGetValue(currentOrder.OrderId, out var orderDate)
                    ? null
                    : orderDate.ToString("yyyy-MM-dd"),
                PlannedDeliveryDate = ResolveDisplayDeliveryDate(plannerEvent, currentOrder, scheduled),
                IsScheduled = scheduled,
                ScheduleStatus = scheduled ? "Scheduled" : "Unscheduled",
                CanSchedule = plannerEvent.EventType == ProductionDeliveryPlanEventType.Order,
                ProductQuantities = quantities
                    .OrderBy(x => x.ProductId)
                    .Select(x => new ProductionDeliveryPlanProductQuantityDto
                    {
                        ProductId = x.ProductId,
                        Quantity = x.Quantity
                    })
                    .ToList(),
                StockBefore = before,
                StockAfter = after,
                CreatedAt = plannerEvent.CreatedAt,
                UpdatedAt = plannerEvent.UpdatedAt
            });
        }

        return new ProductionDeliveryPlanDto
        {
            Id = plan.Id,
            Name = plan.Name,
            CreatedAt = plan.CreatedAt,
            UpdatedAt = plan.UpdatedAt,
            Products = context.Products.ToList(),
            Events = eventDtos
        };
    }

    private async Task<IReadOnlyCollection<ProductionDeliveryPlanProductQuantityDto>> GetEventQuantitiesAsync(
        ProductionDeliveryPlanEvent plannerEvent,
        IReadOnlyDictionary<int, ProductionOrderDto> orderById,
        CancellationToken cancellationToken)
    {
        if (plannerEvent.EventType == ProductionDeliveryPlanEventType.Order)
        {
            if (!plannerEvent.OrderId.HasValue || !orderById.TryGetValue(plannerEvent.OrderId.Value, out var order))
            {
                return Array.Empty<ProductionDeliveryPlanProductQuantityDto>();
            }

            return order.Items
                .GroupBy(x => x.ProductId)
                .Select(group => new ProductionDeliveryPlanProductQuantityDto
                {
                    ProductId = group.Key,
                    Quantity = group.Sum(x => x.Quantity)
                })
                .ToList();
        }

        return await Task.FromResult(
            plannerEvent.Lines
                .OrderBy(x => x.ProductId)
                .Select(x => new ProductionDeliveryPlanProductQuantityDto
                {
                    ProductId = x.ProductId,
                    Quantity = x.Quantity
                })
                .ToList()
        );
    }

    private async Task<IReadOnlyDictionary<int, DeliverySchedule>> LoadSchedulesByOrderIdAsync(IReadOnlyCollection<int> orderIds, CancellationToken cancellationToken)
    {
        if (orderIds.Count == 0)
        {
            return new Dictionary<int, DeliverySchedule>();
        }

        return await _dbContext.DeliverySchedules
            .AsNoTracking()
            .Where(x => orderIds.Contains(x.OrderId))
            .ToDictionaryAsync(x => x.OrderId, cancellationToken);
    }

    private static List<ProductionDeliveryPlanProductDto> BuildProductCatalog(IEnumerable<ProductionOrderDto> orders)
    {
        return orders
            .SelectMany(order => order.Items)
            .GroupBy(item => item.ProductId)
            .Select(group => new ProductionDeliveryPlanProductDto
            {
                ProductId = group.Key,
                ProductCode = group.Select(x => x.ProductCode).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty,
                ProductName = group.Select(x => x.ProductName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty
            })
            .OrderBy(x => x.ProductName)
            .ThenBy(x => x.ProductCode)
            .ThenBy(x => x.ProductId)
            .ToList();
    }

    private static void ApplyEventQuantities(
        ProductionDeliveryPlanEventType eventType,
        IDictionary<int, decimal> runningStock,
        IReadOnlyCollection<ProductionDeliveryPlanProductQuantityDto> quantities)
    {
        foreach (var quantity in quantities)
        {
            runningStock.TryAdd(quantity.ProductId, 0m);

            switch (eventType)
            {
                case ProductionDeliveryPlanEventType.OpeningStock:
                    runningStock[quantity.ProductId] = quantity.Quantity;
                    break;
                case ProductionDeliveryPlanEventType.Order:
                    runningStock[quantity.ProductId] -= quantity.Quantity;
                    break;
                case ProductionDeliveryPlanEventType.Production:
                case ProductionDeliveryPlanEventType.StockAdjustment:
                    runningStock[quantity.ProductId] += quantity.Quantity;
                    break;
            }
        }
    }

    private static List<ProductionDeliveryPlanStockValueDto> CloneStock(IReadOnlyDictionary<int, decimal> stock)
    {
        return stock
            .OrderBy(x => x.Key)
            .Select(x => new ProductionDeliveryPlanStockValueDto
            {
                ProductId = x.Key,
                Quantity = x.Value
            })
            .ToList();
    }

    private static string? ResolveDisplayDeliveryDate(ProductionDeliveryPlanEvent plannerEvent, ProductionOrderDto? currentOrder, bool isScheduled)
    {
        if (plannerEvent.EventType != ProductionDeliveryPlanEventType.Order)
        {
            return null;
        }

        var date = plannerEvent.PlannedDeliveryDate ?? currentOrder?.DeliveryDate;
        if (date is null)
        {
            return null;
        }

        var effective = isScheduled && currentOrder is not null ? currentOrder.DeliveryDate : date.Value;
        return effective.ToString("yyyy-MM-dd");
    }

    private static DateTime ResolvePlannedDeliveryDate(ProductionOrderDto order, bool isScheduled)
    {
        var source = isScheduled ? order.DeliveryDate : order.DeliveryDate;
        return DateTime.SpecifyKind(source.Date, DateTimeKind.Unspecified);
    }

    private async Task ShiftSequencesAsync(int planId, int startingSequence, CancellationToken cancellationToken)
    {
        var events = await _dbContext.ProductionDeliveryPlanEvents
            .Where(x => x.PlanId == planId && x.Sequence >= startingSequence)
            .OrderByDescending(x => x.Sequence)
            .ToListAsync(cancellationToken);

        foreach (var plannerEvent in events)
        {
            plannerEvent.Sequence += 1;
            plannerEvent.UpdatedAt = Now();
        }
    }

    private async Task RenumberEventsAsync(int planId, CancellationToken cancellationToken)
    {
        var events = await _dbContext.ProductionDeliveryPlanEvents
            .Where(x => x.PlanId == planId)
            .OrderBy(x => x.Sequence)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var sequence = 1;
        foreach (var plannerEvent in events)
        {
            plannerEvent.Sequence = sequence++;
            plannerEvent.UpdatedAt = Now();
        }
    }

    private static Task TouchPlanAsync(ProductionDeliveryPlan plan)
    {
        plan.UpdatedAt = Now();
        return Task.CompletedTask;
    }

    private static DateTime Now()
    {
        return DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
    }

    private sealed record PlannerContext(
        ProductionDeliveryPlan Plan,
        IReadOnlyList<ProductionOrderDto> EligibleOrders,
        IReadOnlyList<ProductionDeliveryPlanProductDto> Products,
        IReadOnlyDictionary<int, DateTime> OrderDatesByOrderId,
        IReadOnlyDictionary<int, DeliverySchedule> SchedulesByOrderId);
}