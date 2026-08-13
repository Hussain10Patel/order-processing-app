using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;
using OrderProcessingApp.Services;
using Xunit;

namespace OrderProcessingApp.Tests;

public class ProductionDemandSchedulingTests
{
    [Fact]
    public async Task ScheduledOrder_ContributesToProductionDemand()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var date = new DateTime(2026, 3, 10);

        await fixture.AddOrderAsync("PO-1", OrderStatus.Approved, 200, date, isScheduled: true);

        var demand = await fixture.GetDemandAsync(date);

        Assert.Equal(200m, demand);
    }

    [Fact]
    public async Task UnscheduledOrder_DoesNotContributeToProductionDemand()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var date = new DateTime(2026, 3, 10);

        await fixture.AddOrderAsync("PO-2", OrderStatus.Approved, 300, date, isScheduled: false);

        var demand = await fixture.GetDemandAsync(date);

        Assert.Equal(0m, demand);
    }

    [Fact]
    public async Task MultipleScheduledOrders_AggregateDemandByProduct()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var date = new DateTime(2026, 3, 10);

        await fixture.AddOrderAsync("PO-3", OrderStatus.Approved, 120, date, isScheduled: true);
        await fixture.AddOrderAsync("PO-4", OrderStatus.InProduction, 80, date, isScheduled: true);

        var demand = await fixture.GetDemandAsync(date);

        Assert.Equal(200m, demand);
    }

    [Fact]
    public async Task ScheduledAndUnscheduled_OnlyScheduledIncluded()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var date = new DateTime(2026, 3, 10);

        await fixture.AddOrderAsync("PO-5", OrderStatus.Approved, 200, date, isScheduled: true);
        await fixture.AddOrderAsync("PO-6", OrderStatus.Approved, 300, date, isScheduled: false);

        var demand = await fixture.GetDemandAsync(date);

        Assert.Equal(200m, demand);
    }

    [Fact]
    public async Task InactiveScheduledOrder_RemainsExcludedByActiveFilter()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var date = new DateTime(2026, 3, 10);

        await fixture.AddOrderAsync("PO-7", OrderStatus.Approved, 210, date, isScheduled: true, isActive: false);

        var demand = await fixture.GetDemandAsync(date);

        Assert.Equal(0m, demand);
    }

    [Fact]
    public async Task QuantityAdjustedScheduledOrder_UsesCurrentOrderItemQuantity()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var date = new DateTime(2026, 3, 10);

        await fixture.AddOrderAsync("PO-8", OrderStatus.Approved, 333, date, isScheduled: true);

        var demand = await fixture.GetDemandAsync(date);

        Assert.Equal(333m, demand);
    }

    [Fact]
    public async Task QuantityAdjustedUnscheduledOrder_RemainsExcludedUntilScheduled()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var date = new DateTime(2026, 3, 10);

        await fixture.AddOrderAsync("PO-9", OrderStatus.Approved, 444, date, isScheduled: false);

        var demand = await fixture.GetDemandAsync(date);

        Assert.Equal(0m, demand);
    }

    [Fact]
    public async Task OrderBecomingScheduled_StartsContributing()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var date = new DateTime(2026, 3, 10);

        var orderId = await fixture.AddOrderAsync("PO-10", OrderStatus.Approved, 500, date, isScheduled: false);

        var before = await fixture.GetDemandAsync(date);
        Assert.Equal(0m, before);

        await fixture.ScheduleOrderAsync(orderId, date);

        var after = await fixture.GetDemandAsync(date);
        Assert.Equal(500m, after);
    }

    [Fact]
    public async Task OrderLosingSchedule_StopsContributing()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var date = new DateTime(2026, 3, 10);

        var orderId = await fixture.AddOrderAsync("PO-11", OrderStatus.Approved, 510, date, isScheduled: true);

        var before = await fixture.GetDemandAsync(date);
        Assert.Equal(510m, before);

        await fixture.UnscheduleOrderAsync(orderId);

        var after = await fixture.GetDemandAsync(date);
        Assert.Equal(0m, after);
    }

    [Fact]
    public async Task ExistingStatusFiltering_RemainsUnchanged()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var date = new DateTime(2026, 3, 10);

        await fixture.AddOrderAsync("PO-12", OrderStatus.Pending, 100, date, isScheduled: true);
        await fixture.AddOrderAsync("PO-13", OrderStatus.Approved, 150, date, isScheduled: true);

        var demand = await fixture.GetDemandAsync(date);

        Assert.Equal(150m, demand);
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly DbContextOptions<AppDbContext> _options;

        private TestFixture(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public static async Task<TestFixture> CreateAsync()
        {
            var dbName = $"phase2a-tests-{Guid.NewGuid():N}";

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(dbName)
                .Options;

            await using (var db = new AppDbContext(options))
            {
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
                    Name = "Product X",
                    SKUCode = "PX",
                    PalletConversionRate = 1,
                    IsActive = true,
                    IsMapped = true,
                    CreatedAt = DateTime.UtcNow
                });

                await db.SaveChangesAsync();
            }

            return new TestFixture(options);
        }

        public async Task<int> AddOrderAsync(string orderNumber, OrderStatus status, decimal quantity, DateTime date, bool isScheduled, bool isActive = true)
        {
            await using var db = new AppDbContext(_options);

            var order = new Order
            {
                OrderNumber = orderNumber,
                OrderDate = date.AddDays(-1),
                DeliveryDate = date,
                DistributionCentreId = 1,
                Source = OrderSource.CSV,
                Status = status,
                IsActive = isActive,
                TotalPallets = 0,
                TotalValue = 0
            };

            order.Items.Add(new OrderItem
            {
                ProductId = 1,
                ProductCode = "PX",
                ProductName = "Product X",
                Quantity = quantity,
                Price = 1,
                Pallets = 0
            });

            db.Orders.Add(order);
            await db.SaveChangesAsync();

            if (isScheduled)
            {
                db.DeliverySchedules.Add(new DeliverySchedule
                {
                    OrderId = order.Id,
                    DeliveryDate = date,
                    Status = "Scheduled"
                });
                await db.SaveChangesAsync();
            }

            return order.Id;
        }

        public async Task ScheduleOrderAsync(int orderId, DateTime date)
        {
            await using var db = new AppDbContext(_options);
            db.DeliverySchedules.Add(new DeliverySchedule
            {
                OrderId = orderId,
                DeliveryDate = date,
                Status = "Scheduled"
            });
            await db.SaveChangesAsync();
        }

        public async Task UnscheduleOrderAsync(int orderId)
        {
            await using var db = new AppDbContext(_options);
            var schedule = await db.DeliverySchedules.FirstAsync(x => x.OrderId == orderId);
            db.DeliverySchedules.Remove(schedule);
            await db.SaveChangesAsync();
        }

        public async Task<decimal> GetDemandAsync(DateTime date)
        {
            await using var db = new AppDbContext(_options);
            var service = new ProductionService(db, NullLogger<ProductionService>.Instance);

            await service.CreateOrUpdatePlanAsync(new ProductionPlanUpsertDto
            {
                ProductId = 1,
                Date = date,
                OpeningStock = 0,
                ProductionQuantity = 0,
                Notes = "test"
            });

            var plans = await service.GetPlansByDateAsync(date);
            return plans.Single(x => x.ProductId == 1).TotalOrderDemand;
        }

        public async ValueTask DisposeAsync()
        {
            await ValueTask.CompletedTask;
        }
    }
}
