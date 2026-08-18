using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;
using OrderProcessingApp.Services;
using System.Text;
using Xunit;

namespace OrderProcessingApp.Tests;

public class ProductionDeliveryPlannerTests
{
    [Fact]
    public async Task OpeningStockPersistsAcrossReloads()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var service = fixture.CreatePlannerService();
        var plan = await service.GetCurrentPlanAsync();
        var opening = Assert.Single(plan.Events, x => x.EventType == "OpeningStock");

        await service.UpdateOpeningStockAsync(new ProductionDeliveryPlanQuantitiesUpdateDto
        {
            Quantities = new List<ProductionDeliveryPlanProductQuantityDto>
            {
                new() { ProductId = fixture.ProductA.Id, Quantity = 856m },
                new() { ProductId = fixture.ProductB.Id, Quantity = 0m }
            }
        });

        var reloaded = await fixture.CreatePlannerService().GetCurrentPlanAsync();
        var reloadedOpening = Assert.Single(reloaded.Events, x => x.EventType == "OpeningStock");

        Assert.Equal(opening.Sequence, reloadedOpening.Sequence);
        Assert.Equal(856m, reloadedOpening.StockAfter.Single(x => x.ProductId == fixture.ProductA.Id).Quantity);
    }

    [Fact]
    public async Task ProductionChangesRecalculateDownstreamStock_AndNegativeStockRemainsVisible()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var service = fixture.CreatePlannerService();
        var plan = await service.GetCurrentPlanAsync();
        var order1 = Assert.Single(plan.Events, x => x.OrderId == fixture.Order1.Id);
        var order2 = Assert.Single(plan.Events, x => x.OrderId == fixture.Order2.Id);

        var production = await service.AddProductionEventAsync(order1.Id);
        var productionEvent = Assert.Single(production.Events, x => x.EventType == "Production");

        await service.UpdateEventQuantitiesAsync(productionEvent.Id, new ProductionDeliveryPlanQuantitiesUpdateDto
        {
            Quantities = new List<ProductionDeliveryPlanProductQuantityDto>
            {
                new() { ProductId = fixture.ProductA.Id, Quantity = 300m }
            }
        });

        var reloaded = await fixture.CreatePlannerService().GetCurrentPlanAsync();
        var reloadedOrder1 = Assert.Single(reloaded.Events, x => x.OrderId == fixture.Order1.Id);
        var reloadedOrder2 = Assert.Single(reloaded.Events, x => x.OrderId == fixture.Order2.Id);

        Assert.Equal(-50m, reloadedOrder1.StockAfter.Single(x => x.ProductId == fixture.ProductA.Id).Quantity);
        Assert.Equal(230m, reloadedOrder2.StockAfter.Single(x => x.ProductId == fixture.ProductA.Id).Quantity);
    }

    [Fact]
    public async Task InsertedProductionBetweenOrdersIsPersistedAndRecalculatesDownstreamStock()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var service = fixture.CreatePlannerService();
        var initialPlan = await service.GetCurrentPlanAsync();
        var order1 = Assert.Single(initialPlan.Events, x => x.OrderId == fixture.Order1.Id);
        var order2 = Assert.Single(initialPlan.Events, x => x.OrderId == fixture.Order2.Id);

        var afterInsert = await service.AddProductionEventAsync(order1.Id);
        var insertedProduction = Assert.Single(afterInsert.Events, x => x.EventType == "Production");
        Assert.Equal(order1.Sequence + 1, insertedProduction.Sequence);

        await service.UpdateEventQuantitiesAsync(insertedProduction.Id, new ProductionDeliveryPlanQuantitiesUpdateDto
        {
            Quantities = new List<ProductionDeliveryPlanProductQuantityDto>
            {
                new() { ProductId = fixture.ProductA.Id, Quantity = 300m }
            }
        });

        var reloaded = await fixture.CreatePlannerService().GetCurrentPlanAsync();
        var reloadedProduction = Assert.Single(reloaded.Events, x => x.EventType == "Production");
        var reloadedOrder2 = Assert.Single(reloaded.Events, x => x.OrderId == fixture.Order2.Id);

        Assert.Equal(250m, reloadedProduction.StockAfter.Single(x => x.ProductId == fixture.ProductA.Id).Quantity);
        Assert.Equal(230m, reloadedOrder2.StockAfter.Single(x => x.ProductId == fixture.ProductA.Id).Quantity);
        Assert.Equal(order2.Sequence + 1, reloadedOrder2.Sequence);
    }

    [Fact]
    public async Task InsertedProductionCanBePlacedBetweenExistingProductionAndOrderAndShiftsSequences()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var service = fixture.CreatePlannerService();
        var initialPlan = await service.GetCurrentPlanAsync();
        var order1 = Assert.Single(initialPlan.Events, x => x.OrderId == fixture.Order1.Id);

        var firstInsert = await service.AddProductionEventAsync(order1.Id);
        var firstProduction = Assert.Single(firstInsert.Events, x => x.EventType == "Production");

        await service.UpdateEventQuantitiesAsync(firstProduction.Id, new ProductionDeliveryPlanQuantitiesUpdateDto
        {
            Quantities = new List<ProductionDeliveryPlanProductQuantityDto>
            {
                new() { ProductId = fixture.ProductA.Id, Quantity = 300m }
            }
        });

        var secondInsert = await service.AddProductionEventAsync(firstProduction.Id);
        var secondProduction = secondInsert.Events.Where(x => x.EventType == "Production").OrderBy(x => x.Sequence).Last();

        await service.UpdateEventQuantitiesAsync(secondProduction.Id, new ProductionDeliveryPlanQuantitiesUpdateDto
        {
            Quantities = new List<ProductionDeliveryPlanProductQuantityDto>
            {
                new() { ProductId = fixture.ProductA.Id, Quantity = 40m }
            }
        });

        var reloaded = await fixture.CreatePlannerService().GetCurrentPlanAsync();
        var productions = reloaded.Events.Where(x => x.EventType == "Production").OrderBy(x => x.Sequence).ToList();
        var reloadedOrder2 = Assert.Single(reloaded.Events, x => x.OrderId == fixture.Order2.Id);

        Assert.Equal(2, productions.Count);
        Assert.Equal(firstProduction.Sequence, productions[0].Sequence);
        Assert.Equal(firstProduction.Sequence + 1, productions[1].Sequence);
        Assert.Equal(250m, productions[0].StockAfter.Single(x => x.ProductId == fixture.ProductA.Id).Quantity);
        Assert.Equal(290m, productions[1].StockAfter.Single(x => x.ProductId == fixture.ProductA.Id).Quantity);
        Assert.Equal(270m, reloadedOrder2.StockAfter.Single(x => x.ProductId == fixture.ProductA.Id).Quantity);
        Assert.Equal(secondInsert.Events.Single(x => x.OrderId == fixture.Order2.Id).Sequence, reloadedOrder2.Sequence);
    }

    [Fact]
    public async Task EditingInsertedProductionRecalculatesDownstreamStock()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var service = fixture.CreatePlannerService();
        var plan = await service.GetCurrentPlanAsync();
        var order1 = Assert.Single(plan.Events, x => x.OrderId == fixture.Order1.Id);

        var afterInsert = await service.AddProductionEventAsync(order1.Id);
        var insertedProduction = Assert.Single(afterInsert.Events, x => x.EventType == "Production");

        await service.UpdateEventQuantitiesAsync(insertedProduction.Id, new ProductionDeliveryPlanQuantitiesUpdateDto
        {
            Quantities = new List<ProductionDeliveryPlanProductQuantityDto>
            {
                new() { ProductId = fixture.ProductA.Id, Quantity = 300m }
            }
        });

        await service.UpdateEventQuantitiesAsync(insertedProduction.Id, new ProductionDeliveryPlanQuantitiesUpdateDto
        {
            Quantities = new List<ProductionDeliveryPlanProductQuantityDto>
            {
                new() { ProductId = fixture.ProductA.Id, Quantity = 150m }
            }
        });

        var reloaded = await fixture.CreatePlannerService().GetCurrentPlanAsync();
        var reloadedProduction = Assert.Single(reloaded.Events, x => x.EventType == "Production");
        var reloadedOrder2 = Assert.Single(reloaded.Events, x => x.OrderId == fixture.Order2.Id);

        Assert.Equal(100m, reloadedProduction.StockAfter.Single(x => x.ProductId == fixture.ProductA.Id).Quantity);
        Assert.Equal(80m, reloadedOrder2.StockAfter.Single(x => x.ProductId == fixture.ProductA.Id).Quantity);
    }

    [Fact]
    public async Task DeletingInsertedProductionRestoresDownstreamStock()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var service = fixture.CreatePlannerService();
        var plan = await service.GetCurrentPlanAsync();
        var order1 = Assert.Single(plan.Events, x => x.OrderId == fixture.Order1.Id);

        var afterInsert = await service.AddProductionEventAsync(order1.Id);
        var insertedProduction = Assert.Single(afterInsert.Events, x => x.EventType == "Production");

        await service.UpdateEventQuantitiesAsync(insertedProduction.Id, new ProductionDeliveryPlanQuantitiesUpdateDto
        {
            Quantities = new List<ProductionDeliveryPlanProductQuantityDto>
            {
                new() { ProductId = fixture.ProductA.Id, Quantity = 300m }
            }
        });

        await service.DeleteEventAsync(insertedProduction.Id);

        var reloaded = await fixture.CreatePlannerService().GetCurrentPlanAsync();
        var reloadedOrder1 = Assert.Single(reloaded.Events, x => x.OrderId == fixture.Order1.Id);
        var reloadedOrder2 = Assert.Single(reloaded.Events, x => x.OrderId == fixture.Order2.Id);

        Assert.Empty(reloaded.Events.Where(x => x.EventType == "Production"));
        Assert.Equal(-50m, reloadedOrder1.StockAfter.Single(x => x.ProductId == fixture.ProductA.Id).Quantity);
        Assert.Equal(-70m, reloadedOrder2.StockAfter.Single(x => x.ProductId == fixture.ProductA.Id).Quantity);
    }

    [Fact]
    public async Task InsertedProductionCanRecoverNegativeStockAndStillAllowsScheduling()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var planner = fixture.CreatePlannerService();
        var plan = await planner.GetCurrentPlanAsync();
        var order1 = Assert.Single(plan.Events, x => x.OrderId == fixture.Order1.Id);

        var afterInsert = await planner.AddProductionEventAsync(order1.Id);
        var insertedProduction = Assert.Single(afterInsert.Events, x => x.EventType == "Production");

        await planner.UpdateEventQuantitiesAsync(insertedProduction.Id, new ProductionDeliveryPlanQuantitiesUpdateDto
        {
            Quantities = new List<ProductionDeliveryPlanProductQuantityDto>
            {
                new() { ProductId = fixture.ProductA.Id, Quantity = 100m }
            }
        });

        var reloaded = await fixture.CreatePlannerService().GetCurrentPlanAsync();
        var reloadedProduction = Assert.Single(reloaded.Events, x => x.EventType == "Production");

        Assert.Equal(50m, reloadedProduction.StockAfter.Single(x => x.ProductId == fixture.ProductA.Id).Quantity);
        Assert.True(reloadedProduction.StockAfter.Single(x => x.ProductId == fixture.ProductA.Id).Quantity > 0m);

        var deliveryService = fixture.CreateDeliveryService();
        var scheduled = await deliveryService.ScheduleDeliveryAsync(fixture.Order1.Id, fixture.Order1.DeliveryDate, "planner schedule");

        Assert.Equal("Scheduled", scheduled.Status);
        Assert.Equal(fixture.Order1.Id, scheduled.OrderId);

        await using var db = fixture.CreateDbContext();
        var schedule = await db.DeliverySchedules.SingleAsync(x => x.OrderId == fixture.Order1.Id);
        Assert.Equal(fixture.Order1.DeliveryDate.Date, schedule.DeliveryDate.Date);
        Assert.Equal(fixture.Order1.DeliveryDate.Date, (await db.Orders.SingleAsync(x => x.Id == fixture.Order1.Id)).DeliveryDate.Date);

        var report = await fixture.CreateReportService().GetSummaryByDeliveryDateAsync(fixture.Order1.DeliveryDate);
        Assert.Contains(report.DeliverySummary, row => row.PoNumber == fixture.Order1.OrderNumber);
    }

    [Fact]
    public async Task ExistingSchedulingFlowUpdatesDeliveryScheduleAndReports()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var planner = fixture.CreatePlannerService();
        var plan = await planner.GetCurrentPlanAsync();
        var orderEvent = Assert.Single(plan.Events, x => x.OrderId == fixture.Order1.Id);

        var insertedProductionPlan = await planner.AddProductionEventAsync(orderEvent.Id);
        var insertedProduction = Assert.Single(insertedProductionPlan.Events, x => x.EventType == "Production");
        await planner.UpdateEventQuantitiesAsync(insertedProduction.Id, new ProductionDeliveryPlanQuantitiesUpdateDto
        {
            Quantities = new List<ProductionDeliveryPlanProductQuantityDto>
            {
                new() { ProductId = fixture.ProductA.Id, Quantity = 300m }
            }
        });

        await planner.UpdateOrderDeliveryDateAsync(orderEvent.Id, new ProductionDeliveryPlanDeliveryDateUpdateDto
        {
            DeliveryDate = fixture.Order1.DeliveryDate
        });

        var deliveryService = fixture.CreateDeliveryService();
        var scheduled = await deliveryService.ScheduleDeliveryAsync(fixture.Order1.Id, fixture.Order1.DeliveryDate, "planner schedule");

        Assert.Equal("Scheduled", scheduled.Status);
        Assert.Equal(fixture.Order1.Id, scheduled.OrderId);

        await using var db = fixture.CreateDbContext();
        var schedule = await db.DeliverySchedules.SingleAsync(x => x.OrderId == fixture.Order1.Id);
        Assert.Equal(fixture.Order1.DeliveryDate.Date, schedule.DeliveryDate.Date);
        Assert.Equal(fixture.Order1.DeliveryDate.Date, (await db.Orders.SingleAsync(x => x.Id == fixture.Order1.Id)).DeliveryDate.Date);

        var report = await fixture.CreateReportService().GetSummaryByDeliveryDateAsync(fixture.Order1.DeliveryDate);
        Assert.Contains(report.DeliverySummary, row => row.PoNumber == fixture.Order1.OrderNumber);
    }

    [Fact]
    public async Task SaveDateUpdatesPlannerAndOrderAndPropagatesAcrossCalendarDeliveryReportsExportsAndScheduling()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var originalDate = fixture.Order1.DeliveryDate.Date;
        var newDate = new DateTime(2026, 8, 20);

        var planner = fixture.CreatePlannerService();
        var initialPlan = await planner.GetCurrentPlanAsync();
        var orderEvent = Assert.Single(initialPlan.Events, x => x.OrderId == fixture.Order1.Id);

        await planner.UpdateOrderDeliveryDateAsync(orderEvent.Id, new ProductionDeliveryPlanDeliveryDateUpdateDto
        {
            DeliveryDate = newDate
        });

        var reloadedPlan = await fixture.CreatePlannerService().GetCurrentPlanAsync();
        var reloadedOrderEvent = Assert.Single(reloadedPlan.Events, x => x.OrderId == fixture.Order1.Id);
        Assert.Equal("2026-08-20", reloadedOrderEvent.PlannedDeliveryDate);

        await using (var db = fixture.CreateDbContext())
        {
            var order = await db.Orders.SingleAsync(x => x.Id == fixture.Order1.Id);
            var plannerOrderEvent = await db.ProductionDeliveryPlanEvents.SingleAsync(x => x.Id == reloadedOrderEvent.Id);

            Assert.Equal(newDate.Date, order.DeliveryDate.Date);
            Assert.Equal(newDate.Date, plannerOrderEvent.PlannedDeliveryDate!.Value.Date);
        }

        var productionService = fixture.CreateProductionService();
        var oldCalendar = await productionService.GetCalendarAsync(originalDate, originalDate);
        Assert.DoesNotContain(oldCalendar.SelectMany(x => x.ScheduledItems).Concat(oldCalendar.SelectMany(x => x.UnscheduledItems)), x => x.OrderId == fixture.Order1.Id);

        var newCalendar = await productionService.GetCalendarAsync(newDate, newDate);
        Assert.Contains(newCalendar.SelectMany(x => x.ScheduledItems).Concat(newCalendar.SelectMany(x => x.UnscheduledItems)), x => x.OrderId == fixture.Order1.Id);

        var deliveryService = fixture.CreateDeliveryService();
        var unscheduled = await deliveryService.GetUnscheduledOrdersByDateAsync(newDate);
        var unscheduledOrder = Assert.Single(unscheduled, x => x.Id == fixture.Order1.Id);
        Assert.Equal("2026-08-20", unscheduledOrder.DeliveryDate);

        var reportService = fixture.CreateReportService();
        var oldDateReport = await reportService.GetSummaryByDeliveryDateAsync(originalDate);
        Assert.DoesNotContain(oldDateReport.DeliverySummary, row => row.PoNumber == fixture.Order1.OrderNumber);

        var newDateReport = await reportService.GetSummaryByDeliveryDateAsync(newDate);
        Assert.Contains(newDateReport.DeliverySummary, row => row.PoNumber == fixture.Order1.OrderNumber);

        var exportService = fixture.CreateExportService();
        var ordersExport = await exportService.ExportOrdersToExcelAsync(newDate);
        var ordersCsv = Encoding.UTF8.GetString(ordersExport.Content);
        Assert.Contains("ORD-100", ordersCsv);
        Assert.Contains("2026-08-20", ordersCsv);

        var pastelExportService = fixture.CreatePastelExportService();
        var pastelExport = await pastelExportService.GenerateInvoiceFileAsync(newDate);
        var pastelCsv = Encoding.UTF8.GetString(pastelExport.Content);
        Assert.Contains("DC North", pastelCsv);
        Assert.Contains("PA", pastelCsv);

        var scheduled = await deliveryService.ScheduleDeliveryAsync(fixture.Order1.Id, newDate, "planner schedule");
        Assert.Equal("Scheduled", scheduled.Status);
        Assert.Equal("2026-08-20", scheduled.DeliveryDate);

        await using (var db = fixture.CreateDbContext())
        {
            var schedule = await db.DeliverySchedules.SingleAsync(x => x.OrderId == fixture.Order1.Id);
            var order = await db.Orders.SingleAsync(x => x.Id == fixture.Order1.Id);
            Assert.Equal(newDate.Date, schedule.DeliveryDate.Date);
            Assert.Equal(newDate.Date, order.DeliveryDate.Date);
        }

        var scheduledRows = await deliveryService.GetScheduleByDateAsync(newDate);
        var scheduledOrder = Assert.Single(scheduledRows, x => x.OrderId == fixture.Order1.Id);
        Assert.Equal("2026-08-20", scheduledOrder.DeliveryDate);

        var deliveryExport = await exportService.ExportDeliveryScheduleAsync(newDate);
        var deliveryCsv = Encoding.UTF8.GetString(deliveryExport.Content);
        Assert.Contains("ORD-100", deliveryCsv);
        Assert.Contains("2026-08-20", deliveryCsv);
    }

    // ----------------------------------------------------------------
    // OWNER ORDER ID
    // ----------------------------------------------------------------

    [Fact]
    public async Task AddProductionEventSetsOwnerOrderId()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var planner = fixture.CreatePlannerService();
        var plan = await planner.GetCurrentPlanAsync();
        var order1Event = Assert.Single(plan.Events, x => x.OrderId == fixture.Order1.Id);

        await planner.AddProductionEventAsync(order1Event.Id);

        await using var db = fixture.CreateDbContext();
        var prodEvent = await db.ProductionDeliveryPlanEvents
            .SingleAsync(x => x.EventType == ProductionDeliveryPlanEventType.Production);

        Assert.Equal(fixture.Order1.Id, prodEvent.OwnerOrderId);
    }

    [Fact]
    public async Task AddStockAdjustmentEventSetsOwnerOrderId()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var planner = fixture.CreatePlannerService();
        var plan = await planner.GetCurrentPlanAsync();
        var order1Event = Assert.Single(plan.Events, x => x.OrderId == fixture.Order1.Id);

        await planner.AddStockAdjustmentEventAsync(order1Event.Id);

        await using var db = fixture.CreateDbContext();
        var adjEvent = await db.ProductionDeliveryPlanEvents
            .SingleAsync(x => x.EventType == ProductionDeliveryPlanEventType.StockAdjustment);

        Assert.Equal(fixture.Order1.Id, adjEvent.OwnerOrderId);
    }

    // ----------------------------------------------------------------
    // ORPHAN CLEANUP
    // ----------------------------------------------------------------

    [Fact]
    public async Task GetCurrentPlanRemovesOrphanOrderEventForInactiveOrder()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        // Create a plan with Order1 event, then soft-delete Order1 directly in DB
        var planner = fixture.CreatePlannerService();
        await planner.GetCurrentPlanAsync(); // seeds plan

        await using (var db = fixture.CreateDbContext())
        {
            var order = await db.Orders.IgnoreQueryFilters().SingleAsync(x => x.Id == fixture.Order1.Id);
            order.IsActive = false;
            await db.SaveChangesAsync();
        }

        // Reload: orphan Order event should be removed
        var reloaded = await fixture.CreatePlannerService().GetCurrentPlanAsync();

        Assert.DoesNotContain(reloaded.Events, x => x.OrderId == fixture.Order1.Id);
        Assert.DoesNotContain(reloaded.Events, x => x.EventType == "Order" && x.OrderNumber == null);
    }

    [Fact]
    public async Task GetCurrentPlanRemovesOwnedProductionEventsWithOrphanOrderEvent()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var planner = fixture.CreatePlannerService();
        var plan = await planner.GetCurrentPlanAsync();
        var order1Event = Assert.Single(plan.Events, x => x.OrderId == fixture.Order1.Id);

        // Add a Production event owned by Order1
        await planner.AddProductionEventAsync(order1Event.Id);

        // Soft-delete Order1 directly
        await using (var db = fixture.CreateDbContext())
        {
            var order = await db.Orders.IgnoreQueryFilters().SingleAsync(x => x.Id == fixture.Order1.Id);
            order.IsActive = false;
            await db.SaveChangesAsync();
        }

        var reloaded = await fixture.CreatePlannerService().GetCurrentPlanAsync();

        // Both the Order event and its owned Production event must be gone
        Assert.DoesNotContain(reloaded.Events, x => x.OrderId == fixture.Order1.Id);
        Assert.DoesNotContain(reloaded.Events, x => x.EventType == "Production");
    }

    [Fact]
    public async Task GetCurrentPlanKeepsUnownedProductionEventsWhenOrphanOrderCleaned()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var planner = fixture.CreatePlannerService();
        var plan = await planner.GetCurrentPlanAsync();
        var order1Event = Assert.Single(plan.Events, x => x.OrderId == fixture.Order1.Id);

        // Add Production after Order1 (will have OwnerOrderId = Order1.Id)
        var afterAdd = await planner.AddProductionEventAsync(order1Event.Id);
        var prod1 = Assert.Single(afterAdd.Events, x => x.EventType == "Production");

        // Manually create an UNOWNED Production event (OwnerOrderId = null) after Order2
        var order2Event = Assert.Single(afterAdd.Events, x => x.OrderId == fixture.Order2.Id);
        await planner.AddProductionEventAsync(order2Event.Id);

        // Set the second production's OwnerOrderId to null to simulate legacy unowned event
        await using (var db = fixture.CreateDbContext())
        {
            var secondProd = await db.ProductionDeliveryPlanEvents
                .Where(x => x.EventType == ProductionDeliveryPlanEventType.Production && x.OwnerOrderId == fixture.Order2.Id)
                .FirstAsync();
            secondProd.OwnerOrderId = null;
            await db.SaveChangesAsync();
        }

        // Soft-delete Order1
        await using (var db = fixture.CreateDbContext())
        {
            var order = await db.Orders.IgnoreQueryFilters().SingleAsync(x => x.Id == fixture.Order1.Id);
            order.IsActive = false;
            await db.SaveChangesAsync();
        }

        var reloaded = await fixture.CreatePlannerService().GetCurrentPlanAsync();

        // Owned production (Order1's) should be gone
        Assert.DoesNotContain(reloaded.Events, x => x.Id == prod1.Id);
        // Unowned production should remain
        Assert.Single(reloaded.Events, x => x.EventType == "Production");
    }

    [Fact]
    public async Task GetCurrentPlanIsIdempotentAfterOrphanCleanup()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var planner = fixture.CreatePlannerService();
        await planner.GetCurrentPlanAsync();

        // Soft-delete Order1
        await using (var db = fixture.CreateDbContext())
        {
            var order = await db.Orders.IgnoreQueryFilters().SingleAsync(x => x.Id == fixture.Order1.Id);
            order.IsActive = false;
            await db.SaveChangesAsync();
        }

        var first = await fixture.CreatePlannerService().GetCurrentPlanAsync();
        var second = await fixture.CreatePlannerService().GetCurrentPlanAsync();

        // Running again must not create any new events or duplicate anything
        Assert.Equal(first.Events.Count, second.Events.Count);
        Assert.DoesNotContain(second.Events, x => x.OrderId == fixture.Order1.Id);
    }

    // ----------------------------------------------------------------
    // ACTUAL ORDER DELETE (via OrderService)
    // ----------------------------------------------------------------

    [Fact]
    public async Task DeleteOrderRemovesPlannerEventAndSequenceIsRepaired()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var planner = fixture.CreatePlannerService();
        await planner.GetCurrentPlanAsync(); // seeds plan

        var orderService = fixture.CreateOrderService();
        await orderService.SoftDeleteOrderAsync(fixture.Order1.Id);

        // Reload plan via fresh planner service
        var reloaded = await fixture.CreatePlannerService().GetCurrentPlanAsync();

        Assert.DoesNotContain(reloaded.Events, x => x.OrderId == fixture.Order1.Id);

        // Sequences must be contiguous with no gaps
        var sequences = reloaded.Events.Select(x => x.Sequence).OrderBy(x => x).ToList();
        for (var i = 0; i < sequences.Count; i++)
        {
            Assert.Equal(i + 1, sequences[i]);
        }
    }

    [Fact]
    public async Task DeleteOrderRemovesOwnedProductionEvents()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var planner = fixture.CreatePlannerService();
        var plan = await planner.GetCurrentPlanAsync();
        var order1Event = Assert.Single(plan.Events, x => x.OrderId == fixture.Order1.Id);

        // Add two Production events for Order1
        await planner.AddProductionEventAsync(order1Event.Id);
        var afterSecond = await planner.GetCurrentPlanAsync();
        var prodEvent = Assert.Single(afterSecond.Events, x => x.EventType == "Production");
        await planner.AddProductionEventAsync(prodEvent.Id);

        var orderService = fixture.CreateOrderService();
        await orderService.SoftDeleteOrderAsync(fixture.Order1.Id);

        await using var db = fixture.CreateDbContext();
        var remainingProduction = await db.ProductionDeliveryPlanEvents
            .Where(x => x.EventType == ProductionDeliveryPlanEventType.Production)
            .ToListAsync();

        Assert.Empty(remainingProduction);
    }

    [Fact]
    public async Task DeleteOrderPreservesUnownedProductionEvents()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var planner = fixture.CreatePlannerService();
        var plan = await planner.GetCurrentPlanAsync();
        var order1Event = Assert.Single(plan.Events, x => x.OrderId == fixture.Order1.Id);

        // Add Production owned by Order1
        await planner.AddProductionEventAsync(order1Event.Id);

        // Manually create an UNOWNED Production event
        await using (var db = fixture.CreateDbContext())
        {
            var planEntity = await db.ProductionDeliveryPlans.FirstAsync();
            var maxSeq = await db.ProductionDeliveryPlanEvents.MaxAsync(x => x.Sequence);
            db.ProductionDeliveryPlanEvents.Add(new ProductionDeliveryPlanEvent
            {
                PlanId = planEntity.Id,
                Sequence = maxSeq + 1,
                EventType = ProductionDeliveryPlanEventType.Production,
                OwnerOrderId = null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var orderService = fixture.CreateOrderService();
        await orderService.SoftDeleteOrderAsync(fixture.Order1.Id);

        await using var db2 = fixture.CreateDbContext();
        var remaining = await db2.ProductionDeliveryPlanEvents
            .Where(x => x.EventType == ProductionDeliveryPlanEventType.Production)
            .ToListAsync();

        // The unowned event must survive
        Assert.Single(remaining);
        Assert.Null(remaining[0].OwnerOrderId);
    }

    [Fact]
    public async Task DeleteOrderRemovesDeliverySchedule()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var planner = fixture.CreatePlannerService();
        await planner.GetCurrentPlanAsync();

        // Schedule Order1 first
        var deliveryService = fixture.CreateDeliveryService();
        await deliveryService.ScheduleDeliveryAsync(fixture.Order1.Id, fixture.Order1.DeliveryDate, null);

        await using (var db = fixture.CreateDbContext())
        {
            Assert.True(await db.DeliverySchedules.AnyAsync(x => x.OrderId == fixture.Order1.Id));
        }

        var orderService = fixture.CreateOrderService();
        await orderService.SoftDeleteOrderAsync(fixture.Order1.Id);

        await using var db2 = fixture.CreateDbContext();
        Assert.False(await db2.DeliverySchedules.AnyAsync(x => x.OrderId == fixture.Order1.Id));
    }

    [Fact]
    public async Task DeletedOrderExcludedFromDeliveryPage()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var deliveryService = fixture.CreateDeliveryService();
        await deliveryService.ScheduleDeliveryAsync(fixture.Order1.Id, fixture.Order1.DeliveryDate, null);

        var orderService = fixture.CreateOrderService();
        await orderService.SoftDeleteOrderAsync(fixture.Order1.Id);

        var schedules = await deliveryService.GetScheduleByDateAsync(fixture.Order1.DeliveryDate);
        Assert.DoesNotContain(schedules, x => x.OrderId == fixture.Order1.Id);
    }

    [Fact]
    public async Task DeletedOrderExcludedFromDeliveryExport()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var deliveryService = fixture.CreateDeliveryService();
        await deliveryService.ScheduleDeliveryAsync(fixture.Order1.Id, fixture.Order1.DeliveryDate, null);

        var orderService = fixture.CreateOrderService();
        await orderService.SoftDeleteOrderAsync(fixture.Order1.Id);

        var exportService = fixture.CreateExportService();
        var export = await exportService.ExportDeliveryScheduleAsync(fixture.Order1.DeliveryDate);
        var csv = Encoding.UTF8.GetString(export.Content);
        Assert.DoesNotContain(fixture.Order1.OrderNumber, csv);
    }

    [Fact]
    public async Task DeletedOrderNotInActiveReports()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var orderService = fixture.CreateOrderService();
        await orderService.SoftDeleteOrderAsync(fixture.Order1.Id);

        var reportService = fixture.CreateReportService();
        var summary = await reportService.GetSummaryByDeliveryDateAsync(fixture.Order1.DeliveryDate);
        Assert.DoesNotContain(summary.DeliverySummary, x => x.PoNumber == fixture.Order1.OrderNumber);
    }

    // ----------------------------------------------------------------
    // REMOVE FROM PLAN / ADD TO PLAN
    // ----------------------------------------------------------------

    [Fact]
    public async Task RemoveFromPlanDoesNotDeleteOrder()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var planner = fixture.CreatePlannerService();
        await planner.GetCurrentPlanAsync();

        await planner.RemoveFromPlanAsync(fixture.Order1.Id);

        await using var db = fixture.CreateDbContext();
        var order = await db.Orders.IgnoreQueryFilters().SingleAsync(x => x.Id == fixture.Order1.Id);
        Assert.True(order.IsActive);
        Assert.True(order.IsExcludedFromPlan);
    }

    [Fact]
    public async Task RemoveFromPlanRemovesPlannerRepresentation()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var planner = fixture.CreatePlannerService();
        await planner.GetCurrentPlanAsync();

        var afterRemove = await planner.RemoveFromPlanAsync(fixture.Order1.Id);
        Assert.DoesNotContain(afterRemove.Events, x => x.OrderId == fixture.Order1.Id);
    }

    [Fact]
    public async Task RemoveFromPlanRemovesOwnedProductionEvents()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var planner = fixture.CreatePlannerService();
        var plan = await planner.GetCurrentPlanAsync();
        var order1Event = Assert.Single(plan.Events, x => x.OrderId == fixture.Order1.Id);

        await planner.AddProductionEventAsync(order1Event.Id);
        await planner.RemoveFromPlanAsync(fixture.Order1.Id);

        await using var db = fixture.CreateDbContext();
        var remaining = await db.ProductionDeliveryPlanEvents
            .Where(x => x.EventType == ProductionDeliveryPlanEventType.Production)
            .ToListAsync();
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task RemoveFromPlanDoesNotReappearOnNextLoad()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var planner = fixture.CreatePlannerService();
        await planner.GetCurrentPlanAsync();
        await planner.RemoveFromPlanAsync(fixture.Order1.Id);

        // Reload — excluded order must NOT auto-reappear
        var reloaded = await fixture.CreatePlannerService().GetCurrentPlanAsync();
        Assert.DoesNotContain(reloaded.Events, x => x.OrderId == fixture.Order1.Id);
    }

    [Fact]
    public async Task AddToPlanRestoresOrderToPlan()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var planner = fixture.CreatePlannerService();
        await planner.GetCurrentPlanAsync();
        await planner.RemoveFromPlanAsync(fixture.Order1.Id);

        var restored = await planner.AddToPlanAsync(fixture.Order1.Id);
        Assert.Single(restored.Events, x => x.OrderId == fixture.Order1.Id);
    }

    [Fact]
    public async Task AddToPlanDoesNotCreateDuplicateEvents()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var planner = fixture.CreatePlannerService();
        await planner.GetCurrentPlanAsync();
        await planner.RemoveFromPlanAsync(fixture.Order1.Id);
        await planner.AddToPlanAsync(fixture.Order1.Id);

        // Calling AddToPlan a second time must not create a duplicate
        var plan = await planner.AddToPlanAsync(fixture.Order1.Id);
        Assert.Single(plan.Events, x => x.OrderId == fixture.Order1.Id);
    }

    // ----------------------------------------------------------------
    // RE-UPLOAD AFTER DELETE
    // ----------------------------------------------------------------

    [Fact]
    public async Task ReUploadAfterDeleteSucceedsAndNewOrderHasCleanPlannerState()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        // Build plan for Order1 with a Production event
        var planner = fixture.CreatePlannerService();
        var plan = await planner.GetCurrentPlanAsync();
        var order1Event = Assert.Single(plan.Events, x => x.OrderId == fixture.Order1.Id);
        await planner.AddProductionEventAsync(order1Event.Id);

        // Delete Order1 via OrderService
        var orderService = fixture.CreateOrderService();
        await orderService.SoftDeleteOrderAsync(fixture.Order1.Id);

        // Re-create an order with the same OrderNumber (simulating CSV re-upload)
        int newOrderId;
        await using (var db = fixture.CreateDbContext())
        {
            var newOrder = new Order
            {
                OrderNumber = fixture.Order1.OrderNumber, // same number
                OrderDate = fixture.Order1.OrderDate,
                DeliveryDate = fixture.Order1.DeliveryDate,
                DistributionCentreId = fixture.Order1.DistributionCentreId,
                Source = OrderSource.CSV,
                Status = OrderStatus.Approved,
                IsActive = true,
                TotalValue = 0m,
                TotalPallets = 0m
            };
            newOrder.Items.Add(new OrderItem
            {
                ProductId = fixture.ProductA.Id,
                ProductCode = fixture.ProductA.SKUCode,
                ProductName = fixture.ProductA.Name,
                Quantity = 100m,
                Price = 1m,
                Pallets = 0m,
                MetadataJson = "{}"
            });
            db.Orders.Add(newOrder);
            await db.SaveChangesAsync();
            newOrderId = newOrder.Id;
        }

        // Old inactive order must not block the new active one
        await using (var db = fixture.CreateDbContext())
        {
            var activeOrders = await db.Orders.Where(x => x.OrderNumber == fixture.Order1.OrderNumber).ToListAsync();
            Assert.Single(activeOrders);
            Assert.Equal(newOrderId, activeOrders[0].Id);
        }

        // New order must have no stale planner events from the old order
        await using (var db = fixture.CreateDbContext())
        {
            var staleEvents = await db.ProductionDeliveryPlanEvents
                .Where(x => x.OrderId == fixture.Order1.Id)
                .ToListAsync();
            Assert.Empty(staleEvents);
        }

        // Loading the plan adds the new order and does not have orphan events
        var freshPlan = await fixture.CreatePlannerService().GetCurrentPlanAsync();
        Assert.Single(freshPlan.Events, x => x.OrderId == newOrderId);
        Assert.DoesNotContain(freshPlan.Events, x => x.OrderId == fixture.Order1.Id);
    }

    // ----------------------------------------------------------------
    // SEQUENCE INTEGRITY AFTER DELETE
    // ----------------------------------------------------------------

    [Fact]
    public async Task DeleteMiddleOrderLeavesContiguousSequence()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var planner = fixture.CreatePlannerService();
        await planner.GetCurrentPlanAsync();

        var orderService = fixture.CreateOrderService();
        await orderService.SoftDeleteOrderAsync(fixture.Order1.Id);

        await using var db = fixture.CreateDbContext();
        var plan = await db.ProductionDeliveryPlans.FirstAsync();
        var sequences = await db.ProductionDeliveryPlanEvents
            .Where(x => x.PlanId == plan.Id)
            .OrderBy(x => x.Sequence)
            .Select(x => x.Sequence)
            .ToListAsync();

        for (var i = 0; i < sequences.Count; i++)
        {
            Assert.Equal(i + 1, sequences[i]);
        }
    }

    [Fact]
    public async Task DeleteLastOrderLeavesContiguousSequence()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var planner = fixture.CreatePlannerService();
        await planner.GetCurrentPlanAsync();

        var orderService = fixture.CreateOrderService();
        await orderService.SoftDeleteOrderAsync(fixture.Order2.Id);

        await using var db = fixture.CreateDbContext();
        var plan = await db.ProductionDeliveryPlans.FirstAsync();
        var sequences = await db.ProductionDeliveryPlanEvents
            .Where(x => x.PlanId == plan.Id)
            .OrderBy(x => x.Sequence)
            .Select(x => x.Sequence)
            .ToListAsync();

        for (var i = 0; i < sequences.Count; i++)
        {
            Assert.Equal(i + 1, sequences[i]);
        }
    }

    // ----------------------------------------------------------------
    // DOWNSTREAM STOCK AFTER DELETE
    // ----------------------------------------------------------------

    [Fact]
    public async Task DeleteOrderRecalculatesDownstreamStockForRemainingOrders()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        // Opening stock: 50 for ProductA
        var planner = fixture.CreatePlannerService();
        var plan = await planner.GetCurrentPlanAsync();

        // Order1 demands 100 → StockAfter = -50
        // Order2 demands 20  → StockAfter = -70
        var order2Event = Assert.Single(plan.Events, x => x.OrderId == fixture.Order2.Id);
        Assert.Equal(-70m, order2Event.StockAfter.Single(x => x.ProductId == fixture.ProductA.Id).Quantity);

        // Delete Order1
        var orderService = fixture.CreateOrderService();
        await orderService.SoftDeleteOrderAsync(fixture.Order1.Id);

        var reloaded = await fixture.CreatePlannerService().GetCurrentPlanAsync();
        var reloadedOrder2 = Assert.Single(reloaded.Events, x => x.OrderId == fixture.Order2.Id);

        // Now only Order2 drawn from opening stock 50 → StockAfter = 30
        Assert.Equal(30m, reloadedOrder2.StockAfter.Single(x => x.ProductId == fixture.ProductA.Id).Quantity);
    }

    // ----------------------------------------------------------------
    // TRANSACTION ROLLBACK SAFETY
    // ----------------------------------------------------------------

    [Fact]
    public async Task SoftDeleteOrderThrowsForProcessedStatus()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        await using (var db = fixture.CreateDbContext())
        {
            var order = await db.Orders.SingleAsync(x => x.Id == fixture.Order1.Id);
            order.Status = OrderStatus.Processed;
            await db.SaveChangesAsync();
        }

        var orderService = fixture.CreateOrderService();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => orderService.SoftDeleteOrderAsync(fixture.Order1.Id));

        // Order must remain active
        await using var db2 = fixture.CreateDbContext();
        var unchanged = await db2.Orders.IgnoreQueryFilters().SingleAsync(x => x.Id == fixture.Order1.Id);
        Assert.True(unchanged.IsActive);
    }

    // ----------------------------------------------------------------
    // CSV RE-UPLOAD — REAL IMPORT PATH
    // ----------------------------------------------------------------

    [Fact]
    public async Task CsvReUploadAfterDeleteUsesRealImportPath()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        const string orderNumber = "ORD-CSV-REUPLOAD-001";

        var csvRow = new CsvOrderRowDto
        {
            FileName = "test.csv",
            RowNumber = 1,
            OrderNumber = orderNumber,
            OrderDate = new DateTime(2026, 8, 10),
            DeliveryDate = new DateTime(2026, 8, 14),
            DistributionCentre = "DC North",        // matches the fixture DC
            ProductCode = fixture.ProductA.SKUCode,  // "PA"
            ProductName = fixture.ProductA.Name,
            Quantity = 50m,
            Price = 1.0m,
            Metadata = new Dictionary<string, string>()
        };

        // ---- FIRST IMPORT ----
        var orderService = fixture.CreateFullOrderService();
        var first = await orderService.CreateOrdersFromCsvRowsAsync(
            new List<CsvOrderRowDto> { csvRow },
            allowDuplicates: false,
            createMissingProducts: false);

        Assert.Equal(1, first.Result.CreatedOrders);
        Assert.Empty(first.Result.ValidationErrors);

        int firstOrderId;
        await using (var db = fixture.CreateDbContext())
        {
            var created = await db.Orders.IgnoreQueryFilters()
                .SingleAsync(x => x.OrderNumber == orderNumber);
            firstOrderId = created.Id;
            Assert.True(created.IsActive);
            Assert.False(created.IsExcludedFromPlan);
        }

        // Approve so it is planner-eligible
        await orderService.ApproveOrderAsync(firstOrderId);

        var planner = fixture.CreatePlannerService();
        var planWithFirst = await planner.GetCurrentPlanAsync();
        Assert.Single(planWithFirst.Events, x => x.OrderId == firstOrderId);

        // ---- DELETE ----
        await orderService.SoftDeleteOrderAsync(firstOrderId);

        await using (var db = fixture.CreateDbContext())
        {
            var inactive = await db.Orders.IgnoreQueryFilters()
                .SingleAsync(x => x.Id == firstOrderId);
            Assert.False(inactive.IsActive);

            // Planner event removed by SoftDeleteOrderAsync
            Assert.False(await db.ProductionDeliveryPlanEvents
                .AnyAsync(x => x.OrderId == firstOrderId));
        }

        // ---- SECOND IMPORT — SAME ORDER NUMBER ----
        var second = await orderService.CreateOrdersFromCsvRowsAsync(
            new List<CsvOrderRowDto> { csvRow },
            allowDuplicates: false,
            createMissingProducts: false);

        // Must succeed — no duplicate-OrderNumber error
        Assert.Equal(1, second.Result.CreatedOrders);
        Assert.Empty(second.Result.ValidationErrors);

        int secondOrderId;
        await using (var db = fixture.CreateDbContext())
        {
            // Old inactive record still present
            var old = await db.Orders.IgnoreQueryFilters().SingleAsync(x => x.Id == firstOrderId);
            Assert.False(old.IsActive);

            // New active record exists
            var activeList = await db.Orders
                .Where(x => x.OrderNumber == orderNumber)
                .ToListAsync();
            var newOrder = Assert.Single(activeList);
            secondOrderId = newOrder.Id;
            Assert.NotEqual(firstOrderId, secondOrderId);
            Assert.True(newOrder.IsActive);
            Assert.False(newOrder.IsExcludedFromPlan);

            // No stale planner events from the old order
            Assert.False(await db.ProductionDeliveryPlanEvents
                .AnyAsync(x => x.OrderId == firstOrderId));
        }

        // Approve new order and confirm it appears in the planner
        await orderService.ApproveOrderAsync(secondOrderId);

        var freshPlanner = fixture.CreatePlannerService();
        var planWithSecond = await freshPlanner.GetCurrentPlanAsync();

        Assert.Single(planWithSecond.Events, x => x.OrderId == secondOrderId);
        Assert.DoesNotContain(planWithSecond.Events, x => x.OrderId == firstOrderId);
    }

    // ----------------------------------------------------------------
    // STOCK ADJUSTMENT OWNED DELETE
    // ----------------------------------------------------------------

    [Fact]
    public async Task DeleteOrderRemovesOwnedStockAdjustmentAndProductionEvents()
    {
        await using var fixture = await PlannerFixture.CreateAsync();

        var planner = fixture.CreatePlannerService();
        var plan = await planner.GetCurrentPlanAsync();
        var order1Event = Assert.Single(plan.Events, x => x.OrderId == fixture.Order1.Id);

        // Add an owned Production event and an owned StockAdjustment event for Order1
        await planner.AddProductionEventAsync(order1Event.Id);
        var planAfterProd = await planner.GetCurrentPlanAsync();
        var prodEvent = Assert.Single(planAfterProd.Events, x => x.EventType == "Production");

        await planner.AddStockAdjustmentEventAsync(order1Event.Id);
        var planAfterAdj = await planner.GetCurrentPlanAsync();
        Assert.Single(planAfterAdj.Events, x => x.EventType == "StockAdjustment");

        // Confirm both are owned by Order1
        await using (var db = fixture.CreateDbContext())
        {
            var ownedProd = await db.ProductionDeliveryPlanEvents
                .SingleAsync(x => x.EventType == ProductionDeliveryPlanEventType.Production);
            Assert.Equal(fixture.Order1.Id, ownedProd.OwnerOrderId);

            var ownedAdj = await db.ProductionDeliveryPlanEvents
                .SingleAsync(x => x.EventType == ProductionDeliveryPlanEventType.StockAdjustment);
            Assert.Equal(fixture.Order1.Id, ownedAdj.OwnerOrderId);
        }

        // Also create an UNOWNED legacy Production event (OwnerOrderId = null)
        await using (var db = fixture.CreateDbContext())
        {
            var planEntity = await db.ProductionDeliveryPlans.FirstAsync();
            var maxSeq = await db.ProductionDeliveryPlanEvents.MaxAsync(x => x.Sequence);
            db.ProductionDeliveryPlanEvents.Add(new ProductionDeliveryPlanEvent
            {
                PlanId = planEntity.Id,
                Sequence = maxSeq + 1,
                EventType = ProductionDeliveryPlanEventType.Production,
                OwnerOrderId = null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // Delete Order1 via real SoftDeleteOrderAsync
        var orderService = fixture.CreateOrderService();
        await orderService.SoftDeleteOrderAsync(fixture.Order1.Id);

        await using var db2 = fixture.CreateDbContext();

        // Order event removed
        Assert.False(await db2.ProductionDeliveryPlanEvents
            .AnyAsync(x => x.OrderId == fixture.Order1.Id));

        // Owned Production event removed
        Assert.False(await db2.ProductionDeliveryPlanEvents
            .AnyAsync(x => x.Id == prodEvent.Id));

        // Owned StockAdjustment event removed
        Assert.False(await db2.ProductionDeliveryPlanEvents
            .AnyAsync(x => x.EventType == ProductionDeliveryPlanEventType.StockAdjustment));

        // Unowned Production event (OwnerOrderId = null) survives
        var surviving = await db2.ProductionDeliveryPlanEvents
            .Where(x => x.EventType == ProductionDeliveryPlanEventType.Production)
            .ToListAsync();
        Assert.Single(surviving);
        Assert.Null(surviving[0].OwnerOrderId);

        // Remaining sequences are contiguous
        var plan2 = await db2.ProductionDeliveryPlans.FirstAsync();
        var seqs = await db2.ProductionDeliveryPlanEvents
            .Where(x => x.PlanId == plan2.Id)
            .OrderBy(x => x.Sequence)
            .Select(x => x.Sequence)
            .ToListAsync();
        for (var i = 0; i < seqs.Count; i++)
        {
            Assert.Equal(i + 1, seqs[i]);
        }
    }

    private sealed class PlannerFixture : IAsyncDisposable    {
        public DbContextOptions<AppDbContext> Options { get; }
        public Product ProductA { get; }
        public Product ProductB { get; }
        public Order Order1 { get; }
        public Order Order2 { get; }

        private PlannerFixture(DbContextOptions<AppDbContext> options, Product productA, Product productB, Order order1, Order order2)
        {
            Options = options;
            ProductA = productA;
            ProductB = productB;
            Order1 = order1;
            Order2 = order2;
        }

        public static async Task<PlannerFixture> CreateAsync()
        {
            var dbName = $"planner-tests-{Guid.NewGuid():N}";
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            await using var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var region = new Region { Name = "Region 1" };
            db.Regions.Add(region);
            await db.SaveChangesAsync();

            var dc = new DistributionCentre { Name = "DC North", Code = "DC-N", RegionId = region.Id, IsActive = true };
            db.DistributionCentres.Add(dc);

            var productA = new Product { Name = "Product A", SKUCode = "PA", PalletConversionRate = 1m, IsMapped = true, IsActive = true, CreatedAt = DateTime.UtcNow };
            var productB = new Product { Name = "Product B", SKUCode = "PB", PalletConversionRate = 1m, IsMapped = true, IsActive = true, CreatedAt = DateTime.UtcNow };
            db.Products.AddRange(productA, productB);
            await db.SaveChangesAsync();

            db.Stocks.AddRange(
                new Stock { ProductId = productA.Id, Quantity = 50m, LastUpdated = DateTime.UtcNow },
                new Stock { ProductId = productB.Id, Quantity = 0m, LastUpdated = DateTime.UtcNow });

            var order1 = new Order
            {
                OrderNumber = "ORD-100",
                OrderDate = new DateTime(2026, 8, 10),
                DeliveryDate = new DateTime(2026, 8, 14),
                DistributionCentreId = dc.Id,
                Source = OrderSource.CSV,
                Status = OrderStatus.Approved,
                IsActive = true,
                TotalValue = 0m,
                TotalPallets = 0m
            };
            order1.Items.Add(new OrderItem { ProductId = productA.Id, ProductCode = productA.SKUCode, ProductName = productA.Name, Quantity = 100m, Price = 1m, Pallets = 0m, MetadataJson = "{}" });

            var order2 = new Order
            {
                OrderNumber = "ORD-101",
                OrderDate = new DateTime(2026, 8, 11),
                DeliveryDate = new DateTime(2026, 8, 15),
                DistributionCentreId = dc.Id,
                Source = OrderSource.CSV,
                Status = OrderStatus.Approved,
                IsActive = true,
                TotalValue = 0m,
                TotalPallets = 0m
            };
            order2.Items.Add(new OrderItem { ProductId = productA.Id, ProductCode = productA.SKUCode, ProductName = productA.Name, Quantity = 20m, Price = 1m, Pallets = 0m, MetadataJson = "{}" });

            db.Orders.AddRange(order1, order2);
            await db.SaveChangesAsync();

            return new PlannerFixture(options, productA, productB, order1, order2);
        }

        public AppDbContext CreateDbContext() => new(Options);

        public ProductionDeliveryPlannerService CreatePlannerService()
        {
            var db = new AppDbContext(Options);
            return new ProductionDeliveryPlannerService(db, new StubProductionService(db));
        }

        public OrderService CreateOrderService()
        {
            var db = new AppDbContext(Options);
            return new OrderService(
                db,
                null!,
                null!,
                null!,
                null!,
                null!,
                new AuditService(db),
                NullLogger<OrderService>.Instance);
        }

        /// <summary>
        /// Creates a fully-wired OrderService using real service implementations,
        /// suitable for testing CreateOrdersFromCsvRowsAsync and ApproveOrderAsync.
        /// </summary>
        public OrderService CreateFullOrderService()
        {
            var db = new AppDbContext(Options);
            var pricingService = new PricingService(db, NullLogger<PricingService>.Instance);
            var palletService = new PalletService(db);
            var planningService = new PlanningService(db);
            var distributionCentreResolver = new DistributionCentreResolver(db, NullLogger<DistributionCentreResolver>.Instance);
            var stockService = new StockService(db);
            var auditService = new AuditService(db);
            return new OrderService(
                db,
                pricingService,
                palletService,
                planningService,
                distributionCentreResolver,
                stockService,
                auditService,
                NullLogger<OrderService>.Instance);
        }

        public DeliveryService CreateDeliveryService()
        {
            var db = new AppDbContext(Options);
            return new DeliveryService(db, new AuditService(db), NullLogger<DeliveryService>.Instance);
        }

        public ReportService CreateReportService()
        {
            var db = new AppDbContext(Options);
            return new ReportService(db);
        }

        public ProductionService CreateProductionService()
        {
            var db = new AppDbContext(Options);
            return new ProductionService(db, NullLogger<ProductionService>.Instance);
        }

        public ExportService CreateExportService()
        {
            var db = new AppDbContext(Options);
            return new ExportService(db);
        }

        public PastelExportService CreatePastelExportService()
        {
            var db = new AppDbContext(Options);
            return new PastelExportService(db);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class StubProductionService : IProductionService
        {
            private readonly AppDbContext _db;

            public StubProductionService(AppDbContext db)
            {
                _db = db;
            }

            public async Task<ProductionResponseDto> GetProductionAsync(DateTime? date, CancellationToken cancellationToken = default)
            {
                var orders = await _db.Orders
                    .AsNoTracking()
                    .Include(x => x.DistributionCentre)
                    .Include(x => x.Items)
                        .ThenInclude(x => x.Product)
                    .Where(x => (x.Status == OrderStatus.Approved || x.Status == OrderStatus.InProduction || x.Status == OrderStatus.Processed)
                             && !x.IsExcludedFromPlan)
                    .OrderBy(x => x.DeliveryDate)
                    .ThenBy(x => x.OrderNumber)
                    .ToListAsync(cancellationToken);

                return new ProductionResponseDto
                {
                    Orders = orders.Select(order => new ProductionOrderDto
                    {
                        OrderId = order.Id,
                        OrderNumber = order.OrderNumber,
                        DeliveryDate = order.DeliveryDate,
                        DistributionCentreId = order.DistributionCentreId,
                        DistributionCentre = order.DistributionCentre?.Name ?? string.Empty,
                        Status = order.Status.ToString(),
                        IsProcessed = order.Status == OrderStatus.Processed,
                        Items = order.Items.Select(item => new ProductionOrderItemDto
                        {
                            OrderItemId = item.Id,
                            ProductId = item.ProductId,
                            ProductCode = item.ProductCode ?? item.Product?.SKUCode ?? string.Empty,
                            ProductName = item.ProductName ?? item.Product?.Name ?? string.Empty,
                            Quantity = item.Quantity,
                            Pallets = item.Pallets,
                            CurrentStock = 0m,
                            RequiredStock = item.Quantity,
                            Difference = 0m,
                            ProductionRequired = 0m,
                            RemainingStock = 0m
                        }).ToList()
                    }).ToList()
                };
            }

            public Task<ProductionResponseDto> GetProductionByDateAsync(DateTime date, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task<List<ProductionCalendarDayDto>> GetCalendarAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task<List<ProductionPlanDto>> CreateAsync(List<int> orderIds, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task<ProductionDecisionResultDto> SaveProductionDecisionsAsync(SaveProductionDecisionsDto dto, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task CreateOrUpdatePlanAsync(ProductionPlanUpsertDto dto, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task<List<ProductionPlanDto>> GetPlansByDateAsync(DateTime date, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task<List<StockCheckDto>> CheckStockAsync(DateTime date, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        }
    }
}