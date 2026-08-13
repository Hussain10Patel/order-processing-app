using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;
using OrderProcessingApp.Services;
using Xunit;

namespace OrderProcessingApp.Tests;

public class DeliveryUnscheduleTests
{
    [Fact]
    public async Task Unschedule_RemovesDeliverySchedule_ButKeepsOrderItemsAndDecisions()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var deliveryDate = new DateTime(2026, 8, 20);

        var seeded = await fixture.AddOrderAsync(
            orderNumber: "UNS-100",
            status: OrderStatus.Processed,
            quantity: 150m,
            price: 12.34m,
            deliveryDate: deliveryDate,
            isScheduled: true,
            withDecision: true);

        var removed = await fixture.DeliveryService.UnscheduleDeliveryAsync(seeded.OrderId);

        Assert.True(removed);

        await using var db = fixture.CreateDbContext();
        var order = await db.Orders
            .Include(x => x.Items)
                .ThenInclude(x => x.Product)
            .Include(x => x.Items)
                .ThenInclude(x => x.ProductionDecisions)
            .FirstOrDefaultAsync(x => x.Id == seeded.OrderId);

        Assert.NotNull(order);
        var item = Assert.Single(order!.Items);

        Assert.Equal(150m, item.Quantity);
        Assert.Equal(12.34m, item.Price);
        Assert.Equal(seeded.ProductId, item.ProductId);
        Assert.Equal("SKU-1", item.ProductCode);
        Assert.Equal("Product 1", item.ProductName);
        Assert.Equal(deliveryDate, order.DeliveryDate);

        var decision = Assert.Single(item.ProductionDecisions);
        Assert.Equal(150m, decision.RequiredStock);
        Assert.Equal(200m, decision.CurrentStock);
        Assert.Equal(50m, decision.RemainingStock);
        Assert.Equal(0m, decision.RequiredProductionQty);

        var scheduleCount = await db.DeliverySchedules.CountAsync(x => x.OrderId == seeded.OrderId);
        Assert.Equal(0, scheduleCount);
    }

    [Fact]
    public async Task UnscheduledOrder_AppearsInUnscheduledQuery_AndStopsScheduledDemandContribution()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var deliveryDate = new DateTime(2026, 8, 21);

        var seeded = await fixture.AddOrderAsync(
            orderNumber: "UNS-200",
            status: OrderStatus.Approved,
            quantity: 200m,
            price: 10m,
            deliveryDate: deliveryDate,
            isScheduled: true,
            withDecision: false);

        var demandBefore = await fixture.GetDemandAsync(deliveryDate);
        Assert.Equal(200m, demandBefore);

        await fixture.DeliveryService.UnscheduleDeliveryAsync(seeded.OrderId);

        var unscheduled = await fixture.DeliveryService.GetUnscheduledOrdersByDateAsync(deliveryDate);
        Assert.Contains(unscheduled, x => x.Id == seeded.OrderId);

        var demandAfter = await fixture.GetDemandAsync(deliveryDate);
        Assert.Equal(0m, demandAfter);
    }

    [Fact]
    public async Task UnscheduledOrder_CanBeScheduledAgain_UsingExistingFlow()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var originalDate = new DateTime(2026, 8, 22);
        var rescheduledDate = new DateTime(2026, 8, 23);

        var seeded = await fixture.AddOrderAsync(
            orderNumber: "UNS-300",
            status: OrderStatus.Processed,
            quantity: 80m,
            price: 9.5m,
            deliveryDate: originalDate,
            isScheduled: true,
            withDecision: false);

        await fixture.DeliveryService.UnscheduleDeliveryAsync(seeded.OrderId);

        var scheduled = await fixture.DeliveryService.ScheduleDeliveryAsync(seeded.OrderId, rescheduledDate, "rescheduled after unschedule");

        Assert.Equal(seeded.OrderId, scheduled.OrderId);
        Assert.Equal("Scheduled", scheduled.Status);
        Assert.Equal("Processed", scheduled.OrderStatus);

        await using var db = fixture.CreateDbContext();
        var schedule = await db.DeliverySchedules.FirstOrDefaultAsync(x => x.OrderId == seeded.OrderId);
        Assert.NotNull(schedule);
        Assert.Equal(rescheduledDate.Date, schedule!.DeliveryDate.Date);
    }

    [Fact]
    public async Task Unschedule_AlreadyUnscheduled_IsHandledSafely()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var deliveryDate = new DateTime(2026, 8, 24);

        var seeded = await fixture.AddOrderAsync(
            orderNumber: "UNS-400",
            status: OrderStatus.Approved,
            quantity: 40m,
            price: 5m,
            deliveryDate: deliveryDate,
            isScheduled: false,
            withDecision: false);

        var removed = await fixture.DeliveryService.UnscheduleDeliveryAsync(seeded.OrderId);

        Assert.False(removed);

        await using var db = fixture.CreateDbContext();
        var scheduleCount = await db.DeliverySchedules.CountAsync(x => x.OrderId == seeded.OrderId);
        Assert.Equal(0, scheduleCount);
        var orderExists = await db.Orders.AnyAsync(x => x.Id == seeded.OrderId);
        Assert.True(orderExists);
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly DbContextOptions<AppDbContext> _options;
        private readonly AppDbContext _serviceDb;

        private TestFixture(DbContextOptions<AppDbContext> options)
        {
            _options = options;
            _serviceDb = CreateDbContext();
            DeliveryService = new DeliveryService(_serviceDb, new FakeAuditService(), NullLogger<DeliveryService>.Instance);
        }

        public IDeliveryService DeliveryService { get; }

        public static async Task<TestFixture> CreateAsync()
        {
            var dbName = $"unschedule-tests-{Guid.NewGuid():N}";
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            await using var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();

            db.Regions.Add(new Region { Id = 1, Name = "Region 1" });
            db.DistributionCentres.Add(new DistributionCentre
            {
                Id = 1,
                Name = "DC 1",
                Code = "DC1",
                RegionId = 1,
                IsActive = true
            });

            db.Products.Add(new Product
            {
                Id = 1,
                Name = "Product 1",
                SKUCode = "SKU-1",
                PalletConversionRate = 1m,
                IsMapped = true,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });

            db.PriceLists.Add(new PriceList
            {
                ProductId = 1,
                DistributionCentreId = 1,
                Price = 10m,
                IsActive = true
            });

            await db.SaveChangesAsync();

            return new TestFixture(options);
        }

        public AppDbContext CreateDbContext() => new(_options);

        public async Task<(int OrderId, int ProductId)> AddOrderAsync(
            string orderNumber,
            OrderStatus status,
            decimal quantity,
            decimal price,
            DateTime deliveryDate,
            bool isScheduled,
            bool withDecision)
        {
            await using var db = CreateDbContext();

            var order = new Order
            {
                OrderNumber = orderNumber,
                OrderDate = deliveryDate.AddDays(-1),
                DeliveryDate = deliveryDate,
                DistributionCentreId = 1,
                Source = OrderSource.CSV,
                Status = status,
                TotalPallets = quantity,
                TotalValue = quantity * price,
                IsActive = true
            };

            var item = new OrderItem
            {
                ProductId = 1,
                ProductCode = "SKU-1",
                ProductName = "Product 1",
                Quantity = quantity,
                Price = price,
                Pallets = quantity
            };

            order.Items.Add(item);
            db.Orders.Add(order);
            await db.SaveChangesAsync();

            if (isScheduled)
            {
                db.DeliverySchedules.Add(new DeliverySchedule
                {
                    OrderId = order.Id,
                    DeliveryDate = deliveryDate,
                    Status = "Scheduled",
                    Notes = "seeded"
                });
            }

            if (withDecision)
            {
                db.ProductionDecisions.Add(new ProductionDecision
                {
                    OrderItemId = item.Id,
                    IsSufficient = true,
                    RequiredStock = quantity,
                    CurrentStock = 200m,
                    Difference = 50m,
                    RequiredProductionQty = 0m,
                    RemainingStock = 50m,
                    Notes = "seeded"
                });
            }

            await db.SaveChangesAsync();
            return (order.Id, 1);
        }

        public async Task<decimal> GetDemandAsync(DateTime date)
        {
            await using var db = CreateDbContext();
            var productionService = new ProductionService(db, NullLogger<ProductionService>.Instance);

            await productionService.CreateOrUpdatePlanAsync(new ProductionPlanUpsertDto
            {
                ProductId = 1,
                Date = date,
                OpeningStock = 0,
                ProductionQuantity = 0,
                Notes = "demand-check"
            });

            var plans = await productionService.GetPlansByDateAsync(date);
            return plans.Single(x => x.ProductId == 1).TotalOrderDemand;
        }

        public async ValueTask DisposeAsync()
        {
            await _serviceDb.DisposeAsync();
        }
    }

    private sealed class FakeAuditService : IAuditService
    {
        public void TrackChange(string entity, int entityId, string field, string? oldValue, string? newValue, string changedBy = "System")
        {
        }

        public Task LogChangeAsync(string entity, int entityId, string field, string? oldValue, string? newValue, string changedBy = "System", CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<List<AuditLogDto>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new List<AuditLogDto>());
        }

        public Task<List<AuditLogDto>> GetByEntityAsync(string entity, int entityId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new List<AuditLogDto>());
        }
    }
}
