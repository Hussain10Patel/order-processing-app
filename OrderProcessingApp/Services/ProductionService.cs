using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;

namespace OrderProcessingApp.Services;

public class ProductionService : IProductionService
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<ProductionService> _logger;

    private sealed class ProductionItemCalculation
    {
        public int OrderItemId { get; set; }
        public decimal RequiredStock { get; set; }
        public decimal CurrentStock { get; set; }
        public decimal Difference { get; set; }
        public decimal ComputedProductionRequired { get; set; }
        public decimal RemainingStock { get; set; }
        public bool? DecisionIsSufficient { get; set; }
        public decimal? DecisionRequiredProductionQty { get; set; }
        public decimal DisplayProductionRequired { get; set; }
        public bool UsedPersistedDecisionStock { get; set; }
        public bool UsedManualStock { get; set; }
    }

    public ProductionService(AppDbContext dbContext, ILogger<ProductionService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<ProductionResponseDto> GetProductionAsync(DateTime? date, CancellationToken cancellationToken = default)
    {
        return await GetProductionByOrderAsync(date, cancellationToken);
    }

    private async Task<ProductionResponseDto> GetProductionByOrderAsync(DateTime? planningDate, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[PRODUCTION LOAD START] PlanningDate: {PlanningDate}", planningDate);

        try
        {
            var orphanedDcOrderCount = await _dbContext.Orders
                .IgnoreQueryFilters()
                .AsNoTracking()
                .CountAsync(order => !_dbContext.DistributionCentres.IgnoreQueryFilters().Any(dc => dc.Id == order.DistributionCentreId), cancellationToken);

            var ordersWithoutDcAssignment = await _dbContext.Orders
                .IgnoreQueryFilters()
                .AsNoTracking()
                .CountAsync(order => order.DistributionCentreId <= 0, cancellationToken);

            var duplicateInactiveDcNames = await _dbContext.DistributionCentres
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(dc => !dc.IsActive)
                .GroupBy(dc => (dc.Name ?? string.Empty).Trim().ToLower())
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToListAsync(cancellationToken);

            _logger.LogInformation(
                "[PRODUCTION DC] OrphanedDcOrders: {OrphanedDcOrders}, OrdersWithInvalidDistributionCentreId: {OrdersWithInvalidDistributionCentreId}, DuplicateInactiveDcNames: {DuplicateInactiveDcNames}",
                orphanedDcOrderCount,
                ordersWithoutDcAssignment,
                duplicateInactiveDcNames.Count);

            var orders = await _dbContext.Orders
                .AsNoTracking()
                .Where(x => OrderWorkflowStatusRules.ProductionAndDeliveryQueryableStatuses.Contains(x.Status))
                .Include(x => x.DistributionCentre)
                .Include(x => x.Items)
                    .ThenInclude(x => x.Product)
                .Include(x => x.Items)
                    .ThenInclude(x => x.ProductionDecisions)
                .OrderBy(x => x.DeliveryDate)
                .ThenBy(x => x.OrderNumber)
                .ToListAsync(cancellationToken);

            _logger.LogInformation("[PRODUCTION STATUS FILTER] AllowedStatuses={Statuses}, ReturnedOrders={Count}", OrderWorkflowStatusRules.ProductionAndDeliveryStatusLabel, orders.Count);

            foreach (var order in orders)
            {
                _logger.LogInformation(
                    "[PRODUCTION ORDER] OrderId: {OrderId}, OrderNumber: {OrderNumber}, DistributionCentreId: {DistributionCentreId}, Status: {Status}, Items: {Items}",
                    order.Id,
                    order.OrderNumber,
                    order.DistributionCentreId,
                    order.Status,
                    order.Items.Count);

                _logger.LogInformation(
                    "[PRODUCTION DC] OrderId: {OrderId}, DistributionCentreId: {DistributionCentreId}, DistributionCentreName: {DistributionCentreName}, HasDistributionCentre: {HasDistributionCentre}",
                    order.Id,
                    order.DistributionCentreId,
                    order.DistributionCentre?.Name ?? string.Empty,
                    order.DistributionCentre is not null);

                foreach (var item in order.Items)
                {
                    _logger.LogInformation(
                        "[PRODUCTION ITEM] OrderId: {OrderId}, OrderItemId: {OrderItemId}, ProductId: {ProductId}, ProductName: {ProductName}, HasProduct: {HasProduct}",
                        order.Id,
                        item.Id,
                        item.ProductId,
                        item.Product?.Name ?? item.ProductName ?? string.Empty,
                        item.Product is not null);
                }
            }

            var calculatedByItemId = await BuildProductionSnapshotAsync(orders, planningDate, cancellationToken);

            Console.WriteLine($"[PRODUCTION] Loaded visible orders: {orders.Count}");
            foreach (var order in orders)
            {
                Console.WriteLine($"[PRODUCTION][ORDER] Id={order.Id}, Number={order.OrderNumber}, Status={order.Status}, Items={order.Items.Count}");
            }

            var validOrders = new List<Order>();
            foreach (var order in orders)
            {
                var hasInvalidOrderDc = order.DistributionCentreId <= 0
                    || order.DistributionCentre is null
                    || string.IsNullOrWhiteSpace(order.DistributionCentre.Name);

                var hasInvalidItem = order.Items.Any(item => item.ProductId <= 0 || item.Product is null);
                if (hasInvalidOrderDc || hasInvalidItem)
                {
                    _logger.LogWarning(
                        "[PRODUCTION INVALID ORDER] OrderId: {OrderId}, OrderNumber: {OrderNumber}, DistributionCentreId: {DistributionCentreId}, DistributionCentreName: {DistributionCentreName}",
                        order.Id,
                        order.OrderNumber,
                        order.DistributionCentreId,
                        order.DistributionCentre?.Name ?? string.Empty);
                    continue;
                }

                validOrders.Add(order);
            }

            var orderDtos = validOrders.Select(order =>
            {
                var itemDtos = order.Items
                    .OrderBy(item => item.Id)
                    .Select(item =>
                    {
                        if (!calculatedByItemId.TryGetValue(item.Id, out var calculated))
                        {
                            calculated = new ProductionItemCalculation
                            {
                                OrderItemId = item.Id,
                                RequiredStock = item.Quantity,
                                CurrentStock = 0,
                                Difference = 0,
                                ComputedProductionRequired = item.Quantity,
                                RemainingStock = 0,
                                DecisionIsSufficient = null,
                                DecisionRequiredProductionQty = null,
                                DisplayProductionRequired = item.Quantity
                            };
                        }

                        return new ProductionOrderItemDto
                        {
                            OrderItemId = item.Id,
                            ProductId = item.ProductId,
                            ProductCode = item.ProductCode ?? item.Product?.SKUCode ?? string.Empty,
                            ProductName = item.ProductName ?? item.Product?.Name ?? string.Empty,
                            Quantity = item.Quantity,
                            Pallets = item.Pallets,
                            CurrentStock = calculated.CurrentStock,
                            RequiredStock = calculated.RequiredStock,
                            Difference = calculated.Difference,
                            ProductionRequired = calculated.ComputedProductionRequired,
                            RemainingStock = calculated.RemainingStock,
                            DecisionIsSufficient = calculated.DecisionIsSufficient,
                            DecisionRequiredProductionQty = calculated.DecisionRequiredProductionQty
                        };
                    }).ToList();

                return new ProductionOrderDto
                {
                    OrderId = order.Id,
                    OrderNumber = order.OrderNumber,
                    DeliveryDate = order.DeliveryDate,
                    DistributionCentreId = order.DistributionCentreId,
                    DistributionCentre = order.DistributionCentre?.Name ?? string.Empty,
                    Status = order.Status.ToString(),
                    IsProcessed = order.Status == OrderStatus.Processed,
                    Items = itemDtos
                };
            }).ToList();

            return new ProductionResponseDto
            {
                Orders = orderDtos
            };
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "[PRODUCTION ERROR] Failed while loading production orders.");
            throw;
        }
    }

    public async Task<ProductionResponseDto> GetProductionByDateAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        return await GetProductionByOrderAsync(date, cancellationToken);
    }

    private static OrderStatus GetHighestProductionStatus(IEnumerable<OrderStatus> statuses)
    {
        if (statuses.Any(x => x == OrderStatus.Scheduled))
        {
            return OrderStatus.Scheduled;
        }

        if (statuses.Any(x => x == OrderStatus.Processed))
        {
            return OrderStatus.Processed;
        }

        if (statuses.Any(x => x == OrderStatus.InProduction))
        {
            return OrderStatus.InProduction;
        }

        if (statuses.Any(x => x == OrderStatus.Approved))
        {
            return OrderStatus.Approved;
        }

        return OrderStatus.Pending;
    }

    public async Task<List<ProductionPlanDto>> CreateAsync(List<int> orderIds, CancellationToken cancellationToken = default)
    {
        var orders = await _dbContext.Orders
            .Include(o => o.Items)
                .ThenInclude(i => i.Product)
            .Where(o => orderIds.Contains(o.Id))
            .ToListAsync(cancellationToken);

        var notFound = orderIds.Except(orders.Select(o => o.Id)).ToList();
        if (notFound.Count > 0)
            throw new KeyNotFoundException($"Orders not found: {string.Join(", ", notFound)}.");

        var productionReadyOrders = orders
            .Where(o => o.Status == OrderStatus.Approved)
            .ToList();

        if (productionReadyOrders.Count == 0)
        {
            return new List<ProductionPlanDto>();
        }

        var productionLines = productionReadyOrders
            .SelectMany(o => o.Items.Select(i => new
            {
                i.ProductId,
                ProductName = i.ProductName ?? i.Product?.Name ?? string.Empty,
                Quantity = i.Quantity,
                Pallets = i.Pallets,
                o.DistributionCentreId,
                PlanDate = ToDbDate(o.DeliveryDate)
            }))
            .ToList();

        var groups = productionLines
            .GroupBy(x => new { x.ProductId, x.PlanDate })
            .ToList();

        if (groups.Count == 0)
        {
            return new List<ProductionPlanDto>();
        }

        foreach (var order in productionReadyOrders)
        {
            order.Status = OrderStatus.Processed;
        }

        foreach (var group in groups)
        {
            var productionQty = group.Sum(x => x.Quantity);

            var existing = await _dbContext.ProductionPlans
                .FirstOrDefaultAsync(x => x.ProductId == group.Key.ProductId && x.Date == group.Key.PlanDate, cancellationToken);

            if (existing is null)
            {
                existing = new ProductionPlan
                {
                    ProductId = group.Key.ProductId,
                    Date = group.Key.PlanDate,
                    OpeningStock = 0,
                    ProductionQuantity = productionQty,
                    ClosingStock = productionQty
                };
                _dbContext.ProductionPlans.Add(existing);
            }
            else
            {
                existing.ProductionQuantity = productionQty;
                existing.ClosingStock = existing.OpeningStock + productionQty
                    - await CalculateTotalDemandAsync(existing.ProductId, existing.Date, cancellationToken);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return await GetPlansByDateAsync(groups.Select(g => g.Key.PlanDate).Distinct().First(), cancellationToken);
    }

    public async Task<ProductionDecisionResultDto> SaveProductionDecisionsAsync(SaveProductionDecisionsDto dto, CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.Orders
            .Include(x => x.Items)
                .ThenInclude(x => x.ProductionDecisions)
            .FirstOrDefaultAsync(x => x.Id == dto.OrderId, cancellationToken);

        if (order is null)
        {
            throw new KeyNotFoundException($"Order not found. OrderId={dto.OrderId}.");
        }

        if (!OrderWorkflowStatusRules.IsProductionDecisionEditable(order.Status))
        {
            throw new InvalidOperationException($"Production decisions can only be saved for Approved, InProduction, or Processed orders. Current status: {order.Status}.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var reopenedForEdit = false;
        if (order.Status == OrderStatus.Approved)
        {
            order.Status = OrderStatus.InProduction;
        }
        else if (order.Status == OrderStatus.Processed)
        {
            order.Status = OrderStatus.InProduction;
            reopenedForEdit = true;
            _logger.LogInformation("[PRODUCTION EDIT REOPEN] OrderId={OrderId} reopened from Processed to InProduction for decision update.", order.Id);
        }

        var activeProductionOrders = await _dbContext.Orders
            .Include(x => x.Items)
                .ThenInclude(x => x.ProductionDecisions)
            .Where(x => OrderWorkflowStatusRules.ProductionDemandQueryableStatuses.Contains(x.Status))
            .OrderBy(x => x.DeliveryDate)
            .ThenBy(x => x.OrderNumber)
            .ToListAsync(cancellationToken);

        var manualInitialStockByItemId = new Dictionary<int, decimal>();
        foreach (var decision in dto.Decisions)
        {
            if (!decision.ManualInitialStock.HasValue)
            {
                continue;
            }

            if (decision.ManualInitialStock.Value < 0)
            {
                throw new InvalidOperationException($"Manual initial stock cannot be negative. OrderItemId={decision.OrderItemId}, Value={decision.ManualInitialStock.Value}.");
            }

            manualInitialStockByItemId[decision.OrderItemId] = decision.ManualInitialStock.Value;
            _logger.LogInformation(
                "[PRODUCTION MANUAL STOCK] OrderId={OrderId}, OrderItemId={OrderItemId}, ManualInitialStock={ManualInitialStock}",
                dto.OrderId,
                decision.OrderItemId,
                decision.ManualInitialStock.Value);
        }

        var calculatedByItemId = await BuildProductionSnapshotAsync(activeProductionOrders, null, cancellationToken, manualInitialStockByItemId);

        var itemIds = order.Items.Select(x => x.Id).ToHashSet();
        var duplicateItemIds = dto.Decisions
            .GroupBy(x => x.OrderItemId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateItemIds.Count > 0)
        {
            throw new InvalidOperationException($"Duplicate production decisions found for order item IDs: {string.Join(", ", duplicateItemIds)}.");
        }

        foreach (var decisionDto in dto.Decisions)
        {
            if (!itemIds.Contains(decisionDto.OrderItemId))
            {
                throw new InvalidOperationException($"Order item {decisionDto.OrderItemId} does not belong to order {order.Id}.");
            }

            var existingDecision = await _dbContext.ProductionDecisions
                .FirstOrDefaultAsync(x => x.OrderItemId == decisionDto.OrderItemId, cancellationToken);

            if (!calculatedByItemId.TryGetValue(decisionDto.OrderItemId, out var calculated))
            {
                throw new InvalidOperationException($"Could not calculate production values for order item {decisionDto.OrderItemId}.");
            }

            var persistedProductionRequired = decisionDto.IsSufficient ? 0 : calculated.ComputedProductionRequired;

            _logger.LogInformation(
                "[PRODUCTION CALC RESULT] OrderId={OrderId}, OrderItemId={OrderItemId}, RequiredStock={RequiredStock}, CurrentStock={CurrentStock}, Difference={Difference}, ProductionRequired={ProductionRequired}, RemainingStock={RemainingStock}, IsSufficient={IsSufficient}",
                dto.OrderId,
                decisionDto.OrderItemId,
                calculated.RequiredStock,
                calculated.CurrentStock,
                calculated.Difference,
                persistedProductionRequired,
                calculated.RemainingStock,
                decisionDto.IsSufficient);

            if (existingDecision is null)
            {
                existingDecision = new ProductionDecision
                {
                    OrderItemId = decisionDto.OrderItemId,
                    IsSufficient = decisionDto.IsSufficient,
                    RequiredStock = calculated.RequiredStock,
                    CurrentStock = calculated.CurrentStock,
                    Difference = calculated.Difference,
                    RequiredProductionQty = persistedProductionRequired,
                    RemainingStock = calculated.RemainingStock,
                    Notes = decisionDto.Notes
                };

                _dbContext.ProductionDecisions.Add(existingDecision);
            }
            else
            {
                existingDecision.IsSufficient = decisionDto.IsSufficient;
                existingDecision.RequiredStock = calculated.RequiredStock;
                existingDecision.CurrentStock = calculated.CurrentStock;
                existingDecision.Difference = calculated.Difference;
                existingDecision.RequiredProductionQty = persistedProductionRequired;
                existingDecision.RemainingStock = calculated.RemainingStock;
                existingDecision.Notes = decisionDto.Notes;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var decisionsRecorded = await _dbContext.ProductionDecisions
            .CountAsync(x => x.OrderItem != null && x.OrderItem.OrderId == order.Id, cancellationToken);
        var totalOrderItems = order.Items.Count;

        var decisionsByOrderItem = await _dbContext.ProductionDecisions
            .Where(x => x.OrderItem != null && x.OrderItem.OrderId == order.Id)
            .ToDictionaryAsync(x => x.OrderItemId, cancellationToken);

        var allItemsResolved = order.Items.All(item =>
            decisionsByOrderItem.TryGetValue(item.Id, out var decision)
            && (decision.IsSufficient || decision.RequiredProductionQty >= 0));

        Console.WriteLine($"[PROCESS VALIDATION] OrderId: {order.Id}, AllItemsResolved: {allItemsResolved}");

        if (decisionsRecorded == totalOrderItems && !allItemsResolved)
        {
            throw new InvalidOperationException("All items must be confirmed before processing");
        }

        if (totalOrderItems > 0 && decisionsRecorded == totalOrderItems && allItemsResolved && order.Status != OrderStatus.Processed)
        {
            order.Status = OrderStatus.Processed;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        var lines = dto.Decisions
            .Where(x => calculatedByItemId.ContainsKey(x.OrderItemId))
            .Select(x =>
            {
                var calculated = calculatedByItemId[x.OrderItemId];
                return new ProductionDecisionLineResultDto
                {
                    OrderItemId = x.OrderItemId,
                    CurrentStock = calculated.CurrentStock,
                    RequiredStock = calculated.RequiredStock,
                    RemainingStock = calculated.RemainingStock,
                    Difference = calculated.Difference,
                    RequiredProductionQty = x.IsSufficient ? 0 : calculated.ComputedProductionRequired,
                    IsSufficient = x.IsSufficient
                };
            })
            .OrderBy(x => x.OrderItemId)
            .ToList();

        return new ProductionDecisionResultDto
        {
            OrderId = order.Id,
            Status = order.Status.ToString(),
            DecisionsRecorded = decisionsRecorded,
            TotalOrderItems = totalOrderItems,
            IsProcessed = order.Status == OrderStatus.Processed,
            WasReopenedForEdit = reopenedForEdit,
            Lines = lines
        };
    }

    public async Task CreateOrUpdatePlanAsync(ProductionPlanUpsertDto dto, CancellationToken cancellationToken = default)
    {
        var planDate = ToDbDate(dto.Date);

        var productExists = await _dbContext.Products
            .AsNoTracking()
            .AnyAsync(x => x.Id == dto.ProductId, cancellationToken);

        if (!productExists)
        {
            throw new KeyNotFoundException("Product not found.");
        }

        var totalDemand = await CalculateTotalDemandAsync(dto.ProductId, planDate, cancellationToken);
        var closingStock = dto.OpeningStock + dto.ProductionQuantity - totalDemand;

        var existing = await _dbContext.ProductionPlans
            .FirstOrDefaultAsync(x => x.ProductId == dto.ProductId && x.Date == planDate, cancellationToken);

        if (existing is null)
        {
            existing = new ProductionPlan
            {
                ProductId = dto.ProductId,
                Date = planDate,
                OpeningStock = dto.OpeningStock,
                ProductionQuantity = dto.ProductionQuantity,
                ClosingStock = closingStock,
                Notes = dto.Notes
            };

            _dbContext.ProductionPlans.Add(existing);
        }
        else
        {
            existing.OpeningStock = dto.OpeningStock;
            existing.ProductionQuantity = dto.ProductionQuantity;
            existing.ClosingStock = closingStock;
            existing.Notes = dto.Notes;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<ProductionPlanDto>> GetPlansByDateAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        var planDate = ToDbDate(date);

        var plans = await _dbContext.ProductionPlans
            .AsNoTracking()
            .Include(x => x.Product)
            .Where(x => x.Date == planDate)
            .OrderBy(x => x.Product!.Name)
            .ToListAsync(cancellationToken);

        var output = new List<ProductionPlanDto>();
        foreach (var plan in plans)
        {
            var totalDemand = await CalculateTotalDemandAsync(plan.ProductId, planDate, cancellationToken);
            var closingStock = plan.OpeningStock + plan.ProductionQuantity - totalDemand;

            output.Add(new ProductionPlanDto
            {
                Id = plan.Id,
                ProductId = plan.ProductId,
                ProductName = plan.Product?.Name ?? string.Empty,
                Date = plan.Date.ToString("yyyy-MM-dd"),
                OpeningStock = plan.OpeningStock,
                ProductionQuantity = plan.ProductionQuantity,
                TotalOrderDemand = totalDemand,
                ClosingStock = closingStock,
                HasInsufficientStock = closingStock < 0,
                Notes = plan.Notes
            });
        }

        return output;
    }

    public async Task<List<StockCheckDto>> CheckStockAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        var planDate = ToDbDate(date);

        var demandByProduct = await _dbContext.OrderItems
            .AsNoTracking()
            .Where(ApplyProductionDemandScope(planDate))
            .GroupBy(x => new { x.ProductId, ProductName = x.Product!.Name })
            .Select(g => new
            {
                g.Key.ProductId,
                g.Key.ProductName,
                RequiredQuantity = g.Sum(x => x.Quantity)
            })
            .ToListAsync(cancellationToken);

        var plans = await _dbContext.ProductionPlans
            .AsNoTracking()
            .Where(x => x.Date == planDate)
            .ToListAsync(cancellationToken);

        var result = new List<StockCheckDto>();
        foreach (var demand in demandByProduct)
        {
            var plan = plans.FirstOrDefault(x => x.ProductId == demand.ProductId);
            var available = plan is null ? 0 : plan.OpeningStock + plan.ProductionQuantity;
            var shortfall = demand.RequiredQuantity > available ? demand.RequiredQuantity - available : 0;

            result.Add(new StockCheckDto
            {
                ProductId = demand.ProductId,
                ProductName = demand.ProductName,
                Date = planDate.ToString("yyyy-MM-dd"),
                RequiredQuantity = demand.RequiredQuantity,
                AvailableQuantity = available,
                Shortfall = shortfall,
                IsSufficient = shortfall <= 0
            });
        }

        return result.OrderBy(x => x.ProductName).ToList();
    }

    private async Task<decimal> CalculateTotalDemandAsync(int productId, DateTime date, CancellationToken cancellationToken)
    {
        return await _dbContext.OrderItems
            .AsNoTracking()
            .Where(ApplyProductionDemandScope(date))
            .Where(x => x.ProductId == productId)
            .SumAsync(x => (decimal?)x.Quantity, cancellationToken) ?? 0;
    }

    private static System.Linq.Expressions.Expression<Func<OrderItem, bool>> ApplyProductionDemandScope(DateTime date)
    {
        var targetDate = date.Date;
        return x => x.Order != null
            && x.Order.DeliverySchedules.Any(schedule => schedule.DeliveryDate.Date == targetDate)
            && OrderWorkflowStatusRules.ProductionDemandQueryableStatuses.Contains(x.Order.Status);
    }

    private static DateTime ToDbDate(DateTime input)
    {
        return DateTime.SpecifyKind(input.Date, DateTimeKind.Unspecified);
    }

    private async Task<Dictionary<int, ProductionItemCalculation>> BuildProductionSnapshotAsync(
        List<Order> orderedProductionOrders,
        DateTime? planningDate,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<int, decimal>? manualInitialStockByItemId = null)
    {
        _ = planningDate;

        var productIds = orderedProductionOrders
            .SelectMany(order => order.Items.Select(item => item.ProductId))
            .Distinct()
            .ToList();

        var stockByProduct = productIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await _dbContext.Stocks
                .AsNoTracking()
                .Where(x => productIds.Contains(x.ProductId))
                .ToDictionaryAsync(x => x.ProductId, x => x.Quantity, cancellationToken);

        var calculatedByItemId = new Dictionary<int, ProductionItemCalculation>();

        var itemsByProduct = orderedProductionOrders
            .SelectMany(order => order.Items.Select(item => new { Order = order, Item = item }))
            .GroupBy(x => x.Item.ProductId)
            .ToList();

        foreach (var productGroup in itemsByProduct)
        {
            var productId = productGroup.Key;
            var runningStock = stockByProduct.TryGetValue(productId, out var persistedStock)
                ? persistedStock
                : 0;

            var orderedProductItems = productGroup
                .OrderBy(x => x.Order.DeliveryDate)
                .ThenBy(x => x.Order.OrderNumber)
                .ThenBy(x => x.Item.Id)
                .ToList();

            foreach (var orderedItem in orderedProductItems)
            {
                var item = orderedItem.Item;

                var requiredStock = item.Quantity;
                var beforeStock = runningStock;
                var manualOverrideApplied = false;
                var persistedDecisionStockApplied = false;

                var existingDecision = item.ProductionDecisions?
                    .OrderByDescending(x => x.Id)
                    .FirstOrDefault();

                if (manualInitialStockByItemId is not null
                    && manualInitialStockByItemId.TryGetValue(item.Id, out var manualInitialStock))
                {
                    beforeStock = manualInitialStock;
                    manualOverrideApplied = true;
                }
                else if (existingDecision is not null)
                {
                    beforeStock = existingDecision.CurrentStock;
                    persistedDecisionStockApplied = true;
                }

                var currentStock = beforeStock;

                decimal difference;
                decimal productionRequired;
                decimal remainingStock;

                difference = beforeStock - requiredStock;
                productionRequired = difference < 0 ? Math.Abs(difference) : 0;
                remainingStock = difference;

                // Always roll the computed closing stock forward (clamped at zero) so
                // manual and persisted per-line openings still establish the next default.
                runningStock = Math.Max(remainingStock, 0);

                Console.WriteLine($"[STOCK CASCADE] ProductId: {productId}, Before: {beforeStock}, After: {runningStock}");
                _logger.LogInformation(
                    "[PRODUCTION CALC RESULT] OrderItemId={OrderItemId}, ProductId={ProductId}, ManualOverrideApplied={ManualOverrideApplied}, PersistedDecisionStockApplied={PersistedDecisionStockApplied}, RequiredStock={RequiredStock}, CurrentStock={CurrentStock}, ComputedProductionRequired={ComputedProductionRequired}, RemainingStock={RemainingStock}",
                    item.Id,
                    productId,
                    manualOverrideApplied,
                    persistedDecisionStockApplied,
                    requiredStock,
                    currentStock,
                    productionRequired,
                    remainingStock);

                calculatedByItemId[item.Id] = new ProductionItemCalculation
                {
                    OrderItemId = item.Id,
                    RequiredStock = requiredStock,
                    CurrentStock = currentStock,
                    Difference = difference,
                    ComputedProductionRequired = productionRequired,
                    RemainingStock = remainingStock,
                    DecisionIsSufficient = existingDecision?.IsSufficient,
                    DecisionRequiredProductionQty = existingDecision?.RequiredProductionQty,
                    DisplayProductionRequired = productionRequired,
                    UsedPersistedDecisionStock = persistedDecisionStockApplied,
                    UsedManualStock = manualOverrideApplied
                };
            }
        }

        return calculatedByItemId;
    }
}
