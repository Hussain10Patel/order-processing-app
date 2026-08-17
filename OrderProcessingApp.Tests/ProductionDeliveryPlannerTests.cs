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

    private sealed class PlannerFixture : IAsyncDisposable
    {
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
                    .Where(x => x.Status == OrderStatus.Approved || x.Status == OrderStatus.InProduction || x.Status == OrderStatus.Processed)
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