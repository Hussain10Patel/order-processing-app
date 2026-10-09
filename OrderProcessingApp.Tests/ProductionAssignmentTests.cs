using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderProcessingApp.Controllers;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;
using OrderProcessingApp.Services;
using Xunit;

namespace OrderProcessingApp.Tests;

public class ProductionAssignmentTests
{
    [Fact]
    public async Task GetApprovedOrdersAsync_ExistingDeliveryDateDoesNotImplyAssignment()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        await fixture.AddOrderAsync("UNDATED", OrderStatus.Approved, fixture.North.Id, noDeliveryDate: true);
        await fixture.AddOrderAsync("DATED", OrderStatus.Approved, fixture.North.Id, deliveryDate: new DateTime(2026, 8, 20));
        await fixture.AddOrderAsync("PENDING", OrderStatus.Pending, fixture.North.Id, noDeliveryDate: true);
        await fixture.AddOrderAsync("INACTIVE", OrderStatus.Approved, fixture.North.Id, isActive: false, noDeliveryDate: true);

        var all = await fixture.Service.GetApprovedOrdersAsync("all", null, null, null, null, null, null);
        var unassigned = await fixture.Service.GetApprovedOrdersAsync("unassigned", null, null, null, null, null, null);
        var assigned = await fixture.Service.GetApprovedOrdersAsync("assigned", null, null, null, null, null, null);

        Assert.Equal(2, all.Count);
        Assert.Equal(2, unassigned.Count);
        Assert.Empty(assigned);
        Assert.Contains(unassigned, order => order.OrderNumber == "UNDATED" && order.DeliveryDate is null);
        Assert.Contains(unassigned, order => order.OrderNumber == "DATED" && order.DeliveryDate == "2026-08-20");

        await using (var db = fixture.CreateDbContext())
        {
            var dated = await db.Orders.SingleAsync(order => order.OrderNumber == "DATED");
            dated.IsAssignedToProduction = true;
            await db.SaveChangesAsync();
        }

        var explicitlyAssigned = await fixture.Service.GetApprovedOrdersAsync("assigned", null, null, null, null, null, null);
        Assert.Equal("DATED", Assert.Single(explicitlyAssigned).OrderNumber);
    }

    [Fact]
    public async Task AssignmentController_RequiresNonNullDeliveryDateAndUnassignIsSeparate()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        var order = await fixture.AddOrderAsync("REQUEST", OrderStatus.Approved, fixture.North.Id, noDeliveryDate: true);
        var controller = new ProductionAssignmentController(fixture.Service);

        var missing = JsonSerializer.Deserialize<SetOrderDeliveryDateDto>("{}")!;
        var missingResponse = await controller.SetDeliveryDate(order.Id, missing, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(missingResponse.Result);

        var assignResponse = await controller.SetDeliveryDate(order.Id,
            JsonSerializer.Deserialize<SetOrderDeliveryDateDto>("{\"deliveryDate\":\"2026-10-15\"}")!, CancellationToken.None);
        Assert.IsType<OkObjectResult>(assignResponse.Result);
        Assert.True(((ProductionAssignmentOrderDto)((OkObjectResult)assignResponse.Result!).Value!).IsAssignedToProduction);

        var missingDate = await controller.SetDeliveryDate(order.Id,
            JsonSerializer.Deserialize<SetOrderDeliveryDateDto>("{\"deliveryDate\":null}")!, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(missingDate.Result);

        var clearResponse = await controller.Unassign(order.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(clearResponse.Result);
        Assert.False(((ProductionAssignmentOrderDto)((OkObjectResult)clearResponse.Result!).Value!).IsAssignedToProduction);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(new DateTime(2026, 10, 15), (await db.Orders.SingleAsync(x => x.Id == order.Id)).DeliveryDate);
    }

    [Fact]
    public async Task SetDeliveryDateAsync_PersistsAssignmentDateAndScheduledStatus()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        var order = await fixture.AddOrderAsync("DATE-CHANGE", OrderStatus.Approved, fixture.North.Id, noDeliveryDate: true);

        var firstDate = new DateTime(2026, 10, 15);
        Assert.True((await fixture.Service.SetDeliveryDateAsync(order.Id, firstDate))?.IsAssignedToProduction);
        var newDate = new DateTime(2026, 10, 19);
        var changed = await fixture.Service.SetDeliveryDateAsync(order.Id, newDate);
        Assert.Equal("2026-10-19", changed?.DeliveryDate);

        await using var db = fixture.CreateDbContext();
        var saved = await db.Orders.SingleAsync(x => x.Id == order.Id);
        Assert.Equal(newDate, saved.DeliveryDate);
        Assert.Equal(OrderStatus.Scheduled, saved.Status);
        Assert.Equal(newDate, (await db.DeliverySchedules.SingleAsync()).DeliveryDate);
        Assert.Empty(await db.ProductionDeliveryPlanEvents.ToListAsync());
        Assert.Empty(await db.Stocks.ToListAsync());
        Assert.Empty(await db.ProductionPlans.ToListAsync());
    }

    [Fact]
    public async Task ApprovedOrderWithExistingDeliveryDate_RemainsUnassignedUntilUserAssignsIt()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        var importedDate = new DateTime(2026, 10, 15);
        var order = await fixture.AddOrderAsync("IMPORTED-DATE", OrderStatus.Approved, fixture.North.Id, deliveryDate: importedDate);

        var before = await fixture.Service.GetApprovedOrdersAsync("unassigned", null, order.OrderNumber, null, null, null, null);
        Assert.True(Assert.Single(before).DeliveryDate is not null);
        Assert.False(before[0].IsAssignedToProduction);

        var result = await fixture.Service.SetDeliveryDateAsync(order.Id, importedDate);
        Assert.True(result?.IsAssignedToProduction);

        await using var db = fixture.CreateDbContext();
        var saved = await db.Orders.SingleAsync(x => x.Id == order.Id);
        Assert.Equal(importedDate, saved.DeliveryDate);
        Assert.True(saved.IsAssignedToProduction);
    }

    [Fact]
    public async Task ApprovalResetsExplicitAssignmentButPreservesDeliveryDate()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        var importedDate = new DateTime(2026, 10, 15);
        var order = await fixture.AddOrderAsync("APPROVAL-RESET", OrderStatus.Validated, fixture.North.Id,
            deliveryDate: importedDate, isAssignedToProduction: true);

        var result = await fixture.CreateOrderService().ApproveOrderAsync(order.Id);

        Assert.Equal(OrderStatus.Approved, result?.Status);
        Assert.Equal(importedDate.ToString("yyyy-MM-dd"), result?.DeliveryDate);
        await using var db = fixture.CreateDbContext();
        var saved = await db.Orders.SingleAsync(x => x.Id == order.Id);
        Assert.False(saved.IsAssignedToProduction);
        Assert.Equal(importedDate, saved.DeliveryDate);
    }

    [Fact]
    public async Task UnassignScheduledOrder_KeepsDeliveryDateAndRemovesSchedule()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        var date = new DateTime(2026, 10, 15);
        var order = await fixture.AddOrderAsync("SCHEDULED", OrderStatus.Approved, fixture.North.Id, deliveryDate: date, isAssignedToProduction: true);
        int scheduleId;
        await using (var db = fixture.CreateDbContext())
        {
            var schedule = new DeliverySchedule { OrderId = order.Id, DeliveryDate = date, Status = "Scheduled" };
            db.DeliverySchedules.Add(schedule);
            await db.SaveChangesAsync();
            scheduleId = schedule.Id;
        }

        var controller = new ProductionAssignmentController(fixture.Service);
        var unassignResponse = await controller.Unassign(order.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(unassignResponse.Result);
        Assert.False(((ProductionAssignmentOrderDto)((OkObjectResult)unassignResponse.Result!).Value!).IsAssignedToProduction);

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(date, (await verify.Orders.SingleAsync(x => x.Id == order.Id)).DeliveryDate);
        Assert.False(await verify.DeliverySchedules.AnyAsync(x => x.Id == scheduleId));
    }

    [Fact]
    public async Task UnassigningOrder_PreservesDeliveryDateAndExistingPlannerEvents()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        var order = await fixture.AddOrderAsync("CLEAR-PLANNER", OrderStatus.Approved, fixture.North.Id, noDeliveryDate: true);
        int orderEventId;
        int productionEventId;

        await using (var db = fixture.CreateDbContext())
        {
            var plan = new ProductionDeliveryPlan { Name = "Keep Events", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            var orderEvent = new ProductionDeliveryPlanEvent
            {
                Plan = plan,
                Sequence = 1,
                EventType = ProductionDeliveryPlanEventType.Order,
                OrderId = order.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            var productionEvent = new ProductionDeliveryPlanEvent
            {
                Plan = plan,
                Sequence = 2,
                EventType = ProductionDeliveryPlanEventType.Production,
                OwnerOrderId = order.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            plan.Events.Add(orderEvent);
            plan.Events.Add(productionEvent);
            db.ProductionDeliveryPlans.Add(plan);
            await db.SaveChangesAsync();
            orderEventId = orderEvent.Id;
            productionEventId = productionEvent.Id;
        }

        var date = new DateTime(2026, 10, 15);
        await fixture.Service.SetDeliveryDateAsync(order.Id, date);
        var result = await fixture.Service.UnassignAsync(order.Id);
        Assert.False(result?.IsAssignedToProduction);

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(date, (await verify.Orders.SingleAsync(x => x.Id == order.Id)).DeliveryDate);
        Assert.Equal(orderEventId, (await verify.ProductionDeliveryPlanEvents.SingleAsync(x => x.EventType == ProductionDeliveryPlanEventType.Order)).Id);
        var savedProductionEvent = await verify.ProductionDeliveryPlanEvents.SingleAsync(x => x.Id == productionEventId);
        Assert.Equal(ProductionDeliveryPlanEventType.Production, savedProductionEvent.EventType);
        Assert.Equal(order.Id, savedProductionEvent.OwnerOrderId);
        Assert.Equal(date, (await verify.ProductionDeliveryPlanEvents.SingleAsync(x => x.Id == orderEventId)).PlannedDeliveryDate);
        Assert.Empty(await verify.DeliverySchedules.ToListAsync());
    }

    [Fact]
    public async Task AssignmentController_RejectsMissingInactiveAndNonApprovedOrders()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        var inactive = await fixture.AddOrderAsync("INACTIVE", OrderStatus.Approved, fixture.North.Id, isActive: false, noDeliveryDate: true);
        var pending = await fixture.AddOrderAsync("PENDING", OrderStatus.Pending, fixture.North.Id, noDeliveryDate: true);
        var controller = new ProductionAssignmentController(fixture.Service);
        var dateRequest = new SetOrderDeliveryDateDto { DeliveryDate = new DateTime(2026, 10, 15) };

        Assert.IsType<NotFoundResult>((await controller.SetDeliveryDate(999999, dateRequest, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await controller.SetDeliveryDate(inactive.Id, dateRequest, CancellationToken.None)).Result);
        Assert.IsType<UnprocessableEntityObjectResult>((await controller.SetDeliveryDate(pending.Id, dateRequest, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task AssignmentController_RejectsDeliveryDateBeforeOrderDate()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        var order = await fixture.AddOrderAsync("EARLY-DATE", OrderStatus.Approved, fixture.North.Id,
            orderDate: new DateTime(2026, 10, 10), noDeliveryDate: true);
        var controller = new ProductionAssignmentController(fixture.Service);

        var response = await controller.SetDeliveryDate(order.Id,
            new SetOrderDeliveryDateDto { DeliveryDate = new DateTime(2026, 10, 9) }, CancellationToken.None);

        Assert.IsType<UnprocessableEntityObjectResult>(response.Result);
        await using var db = fixture.CreateDbContext();
        Assert.Null((await db.Orders.SingleAsync(x => x.Id == order.Id)).DeliveryDate);
    }

    [Fact]
    public async Task ProcessOrderAsync_RejectsApprovedOrderWithoutDeliveryDate()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        var order = await fixture.AddOrderAsync("PROCESS-UNDATED", OrderStatus.Approved, fixture.North.Id, noDeliveryDate: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateOrderService().ProcessOrderAsync(order.Id));

        await using var db = fixture.CreateDbContext();
        var unchanged = await db.Orders.SingleAsync(x => x.Id == order.Id);
        Assert.Equal(OrderStatus.Approved, unchanged.Status);
        Assert.Null(unchanged.DeliveryDate);
    }

    [Fact]
    public async Task AssignmentController_RejectsInvalidAssignmentFilterAndDateRange()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        var controller = new ProductionAssignmentController(fixture.Service);

        Assert.IsType<BadRequestObjectResult>((await controller.GetApprovedOrders("sometimes", null, null, null, null, null, null, CancellationToken.None)).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.GetApprovedOrders("all", null, null, new DateTime(2026, 8, 20), new DateTime(2026, 8, 10), null, null, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task GetApprovedOrdersAsync_DateDcSearchAndRangesCombineWithAndLogic()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        await fixture.AddOrderAsync("PO-NORTH-100", OrderStatus.Approved, fixture.North.Id, orderDate: new DateTime(2026, 8, 12), deliveryDate: new DateTime(2026, 8, 18));
        await fixture.AddOrderAsync("PO-NORTH-101", OrderStatus.Approved, fixture.North.Id, noDeliveryDate: true, orderDate: new DateTime(2026, 8, 12));
        await fixture.AddOrderAsync("PO-SOUTH-100", OrderStatus.Approved, fixture.South.Id, orderDate: new DateTime(2026, 8, 12), deliveryDate: new DateTime(2026, 8, 18));
        await fixture.AddOrderAsync("PO-NORTH-OLD", OrderStatus.Approved, fixture.North.Id, orderDate: new DateTime(2026, 7, 12), deliveryDate: new DateTime(2026, 8, 18));

        await using (var db = fixture.CreateDbContext())
        {
            var assignedOrders = await db.Orders.Where(order => order.OrderNumber == "PO-NORTH-100"
                || order.OrderNumber == "PO-SOUTH-100" || order.OrderNumber == "PO-NORTH-OLD").ToListAsync();
            foreach (var assignedOrder in assignedOrders)
            {
                assignedOrder.IsAssignedToProduction = true;
            }
            await db.SaveChangesAsync();
        }

        var result = await fixture.Service.GetApprovedOrdersAsync(
            "assigned", new[] { fixture.North.Id, fixture.South.Id }, "100",
            new DateTime(2026, 8, 1), new DateTime(2026, 8, 31),
            new DateTime(2026, 8, 1), new DateTime(2026, 8, 31));
        var unassignedWithDeliveryRange = await fixture.Service.GetApprovedOrdersAsync(
            "unassigned", null, null, null, null,
            new DateTime(2026, 8, 1), new DateTime(2026, 8, 31));

        Assert.Equal(new[] { "PO-NORTH-100", "PO-SOUTH-100" }, result.Select(x => x.OrderNumber).OrderBy(x => x).ToArray());
        Assert.Empty(unassignedWithDeliveryRange);
    }

    [Fact]
    public async Task AssignmentController_ChangingScheduledOrderDateUpdatesScheduleAndExistingPlannerOrderEventOnly()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        var initialDate = new DateTime(2026, 10, 15);
        var changedDate = new DateTime(2026, 10, 19);
        var order = await fixture.AddOrderAsync("PLANNER-SCHEDULED", OrderStatus.Approved, fixture.North.Id, deliveryDate: initialDate);
        int orderEventId;
        int scheduleId;
        int productionEventId;
        int productionLineId;
        int stockId;
        decimal stockQuantity;
        decimal productionQuantity;
        int productionPlanId;

        await using (var db = fixture.CreateDbContext())
        {
            var product = new Product { Name = "Planner Product", SKUCode = "PLAN-1", PalletConversionRate = 10m, IsMapped = true, IsActive = true, CreatedAt = DateTime.UtcNow };
            db.Products.Add(product);
            await db.SaveChangesAsync();
            var item = new OrderItem { OrderId = order.Id, ProductId = product.Id, ProductCode = product.SKUCode, ProductName = product.Name, Quantity = 30m, Price = 5m, Pallets = 3m };
            db.OrderItems.Add(item);
            var schedule = new DeliverySchedule { OrderId = order.Id, DeliveryDate = initialDate, Status = "Scheduled", Notes = "preserve" };
            db.DeliverySchedules.Add(schedule);
            var plan = new ProductionDeliveryPlan { Name = "Date Sync Plan", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            var orderEvent = new ProductionDeliveryPlanEvent { Plan = plan, Sequence = 1, EventType = ProductionDeliveryPlanEventType.Order, OrderId = order.Id, PlannedDeliveryDate = initialDate, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            var productionEvent = new ProductionDeliveryPlanEvent { Plan = plan, Sequence = 2, EventType = ProductionDeliveryPlanEventType.Production, OwnerOrderId = order.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            plan.Events.Add(orderEvent);
            plan.Events.Add(productionEvent);
            productionEvent.Lines.Add(new ProductionDeliveryPlanEventLine { Product = product, Quantity = 17m, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            var stock = new Stock { ProductId = product.Id, Quantity = 77m, LastUpdated = DateTime.UtcNow };
            var productionPlan = new ProductionPlan { ProductId = product.Id, Date = initialDate, OpeningStock = 20m, ProductionQuantity = 12m, ClosingStock = 2m };
            db.AddRange(plan, stock, productionPlan);
            await db.SaveChangesAsync();
            orderEventId = orderEvent.Id;
            productionEventId = productionEvent.Id;
            productionLineId = productionEvent.Lines.Single().Id;
            scheduleId = schedule.Id;
            stockId = stock.Id;
            stockQuantity = stock.Quantity;
            productionPlanId = productionPlan.Id;
            productionQuantity = productionPlan.ProductionQuantity;
        }

        var controller = new ProductionAssignmentController(fixture.Service);
        var response = await controller.SetDeliveryDate(order.Id, new SetOrderDeliveryDateDto { DeliveryDate = changedDate }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(response.Result);

        await using var verifyDb = fixture.CreateDbContext();
        var savedOrder = await verifyDb.Orders.SingleAsync(x => x.Id == order.Id);
        var savedSchedule = await verifyDb.DeliverySchedules.SingleAsync(x => x.Id == scheduleId);
        var savedOrderEvent = await verifyDb.ProductionDeliveryPlanEvents.SingleAsync(x => x.Id == orderEventId);
        var savedProductionEvent = await verifyDb.ProductionDeliveryPlanEvents.Include(x => x.Lines).SingleAsync(x => x.Id == productionEventId);
        Assert.Equal(changedDate, savedOrder.DeliveryDate);
        Assert.Equal(OrderStatus.Scheduled, savedOrder.Status);
        Assert.Equal(changedDate, savedSchedule.DeliveryDate);
        Assert.Equal("Scheduled", savedSchedule.Status);
        Assert.Equal(changedDate, savedOrderEvent.PlannedDeliveryDate);
        Assert.Equal(ProductionDeliveryPlanEventType.Production, savedProductionEvent.EventType);
        Assert.Equal(order.Id, savedProductionEvent.OwnerOrderId);
        Assert.Equal(productionLineId, savedProductionEvent.Lines.Single().Id);
        Assert.Equal(17m, savedProductionEvent.Lines.Single().Quantity);
        Assert.Equal(stockQuantity, (await verifyDb.Stocks.SingleAsync(x => x.Id == stockId)).Quantity);
        Assert.Equal(productionQuantity, (await verifyDb.ProductionPlans.SingleAsync(x => x.Id == productionPlanId)).ProductionQuantity);
    }

    [Fact]
    public async Task NullDeliveryDate_DoesNotBreakDateBasedReportsOrExports()
    {
        await using var fixture = await AssignmentFixture.CreateAsync();
        await fixture.AddOrderAsync("UNDATED-REPORT", OrderStatus.Approved, fixture.North.Id, noDeliveryDate: true);

        var reportService = new ReportService(fixture.CreateDbContext());
        var availableDates = await reportService.GetAvailableReportDatesAsync();
        var report = await reportService.GetSummaryByDeliveryDateAsync(new DateTime(2026, 10, 15));
        var ordersExport = await new ExportService(fixture.CreateDbContext()).ExportOrdersToExcelAsync(new DateTime(2026, 10, 15));
        var pastelExport = await new PastelExportService(fixture.CreateDbContext()).GenerateInvoiceFileAsync(new DateTime(2026, 10, 15));

        Assert.Empty(availableDates);
        Assert.Equal(0, report.TotalOrders);
        Assert.DoesNotContain("UNDATED-REPORT", System.Text.Encoding.UTF8.GetString(ordersExport.Content));
        Assert.DoesNotContain("UNDATED-REPORT", System.Text.Encoding.UTF8.GetString(pastelExport.Content));
    }

    private sealed class AssignmentFixture : IAsyncDisposable
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public DistributionCentre North { get; private set; } = null!;
        public DistributionCentre South { get; private set; } = null!;
        public DistributionCentre East { get; private set; } = null!;
        public ProductionAssignmentService Service => new(CreateDbContext());

        private AssignmentFixture(DbContextOptions<AppDbContext> options) => _options = options;

        public static async Task<AssignmentFixture> CreateAsync()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"production-assignment-{Guid.NewGuid():N}").Options;
            var fixture = new AssignmentFixture(options);
            await using var db = fixture.CreateDbContext();
            var region = new Region { Name = "Region" };
            db.Regions.Add(region);
            await db.SaveChangesAsync();
            fixture.North = new DistributionCentre { Name = "North", Code = "N", RegionId = region.Id, IsActive = true };
            fixture.South = new DistributionCentre { Name = "South", Code = "S", RegionId = region.Id, IsActive = true };
            fixture.East = new DistributionCentre { Name = "East", Code = "E", RegionId = region.Id, IsActive = true };
            db.DistributionCentres.AddRange(fixture.North, fixture.South, fixture.East);
            await db.SaveChangesAsync();
            return fixture;
        }

        public AppDbContext CreateDbContext() => new(_options);

        public OrderService CreateOrderService()
        {
            var db = CreateDbContext();
            return new OrderService(
                db,
                new PricingService(db, NullLogger<PricingService>.Instance),
                new PalletService(db),
                new PlanningService(db),
                new DistributionCentreResolver(db, NullLogger<DistributionCentreResolver>.Instance),
                new StockService(db),
                new AuditService(db),
                NullLogger<OrderService>.Instance);
        }

        public async Task<Order> AddOrderAsync(string orderNumber, OrderStatus status, int distributionCentreId,
            bool isActive = true, DateTime? orderDate = null, DateTime? deliveryDate = null, bool noDeliveryDate = false,
            bool isAssignedToProduction = false)
        {
            await using var db = CreateDbContext();
            var order = new Order
            {
                OrderNumber = orderNumber,
                OrderDate = orderDate ?? new DateTime(2026, 8, 10),
                DeliveryDate = noDeliveryDate ? null : deliveryDate ?? new DateTime(2026, 8, 20),
                DistributionCentreId = distributionCentreId,
                Status = status,
                Source = OrderSource.CSV,
                IsActive = isActive,
                IsAssignedToProduction = isAssignedToProduction
            };
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            return order;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
