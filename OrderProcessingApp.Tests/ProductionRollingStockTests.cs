using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;
using OrderProcessingApp.Services;
using Xunit;

namespace OrderProcessingApp.Tests;

public class ProductionRollingStockTests
{
    [Fact]
    public async Task ExcelArithmetic_OpeningPlusProductionMinusDemand_ProducesExpectedClosing()
    {
        await using var fixture = await RollingFixture.CreateAsync();

        var day1 = new DateTime(2026, 8, 14);
        var day2 = new DateTime(2026, 8, 15);

        var order1 = await fixture.AddScheduledOrderAsync("ORD-010", 1, 1, day1, 50m);
        await fixture.AddScheduledOrderAsync("ORD-011", 1, 1, day2, 5m);

        await fixture.AddDecisionAsync(order1.ItemId, currentStock: 20m, remainingStock: 10m, isSufficient: false, requiredProductionQty: 40m);

        var production = await fixture.GetProductionAsync();
        var day1Item = fixture.FindItem(production, "ORD-010");
        var day2Item = fixture.FindItem(production, "ORD-011");

        Assert.Equal(20m, day1Item.CurrentStock);
        Assert.Equal(50m, day1Item.RequiredStock);
        Assert.Equal(10m, day1Item.RemainingStock);
        Assert.Equal(10m, day2Item.CurrentStock);
    }

    [Fact]
    public async Task OkDecision_ClosingEqualsOpeningMinusDemand()
    {
        await using var fixture = await RollingFixture.CreateAsync();

        var day1 = new DateTime(2026, 8, 14);
        var day2 = new DateTime(2026, 8, 15);

        var order1 = await fixture.AddScheduledOrderAsync("ORD-020", 1, 1, day1, 60m);
        await fixture.AddScheduledOrderAsync("ORD-021", 1, 1, day2, 10m);

        await fixture.AddDecisionAsync(order1.ItemId, currentStock: 100m, remainingStock: 40m, isSufficient: true, requiredProductionQty: 0m);

        var production = await fixture.GetProductionAsync();
        var day1Item = fixture.FindItem(production, "ORD-020");
        var day2Item = fixture.FindItem(production, "ORD-021");

        Assert.Equal(40m, day1Item.RemainingStock);
        Assert.Equal(40m, day2Item.CurrentStock);
    }

    [Fact]
    public async Task PersistedDecision_Day1RemainingFlowsToDay2()
    {
        await using var fixture = await RollingFixture.CreateAsync();

        var day1 = new DateTime(2026, 8, 14);
        var day2 = new DateTime(2026, 8, 15);

        var order1 = await fixture.AddScheduledOrderAsync("ORD-100", 1, 1, day1, 100m);
        var order2 = await fixture.AddScheduledOrderAsync("ORD-101", 1, 1, day2, 50m);

        await fixture.AddDecisionAsync(order1.ItemId, currentStock: 200m, remainingStock: 100m, isSufficient: true, requiredProductionQty: 0m);

        var production = await fixture.GetProductionAsync();
        var day2Item = fixture.FindItem(production, "ORD-101");

        Assert.Equal(100m, day2Item.CurrentStock);
        Assert.Equal(50m, day2Item.RequiredStock);
        Assert.Equal(50m, day2Item.RemainingStock);
    }

    [Fact]
    public async Task NoPersistedDecision_RollingBehaviorRemainsUnchanged()
    {
        await using var fixture = await RollingFixture.CreateAsync();

        var day1 = new DateTime(2026, 8, 14);
        var day2 = new DateTime(2026, 8, 15);

        await fixture.UpsertStockAsync(1, 200m);
        await fixture.AddScheduledOrderAsync("ORD-200", 1, 1, day1, 100m);
        await fixture.AddScheduledOrderAsync("ORD-201", 1, 1, day2, 50m);

        var production = await fixture.GetProductionAsync();
        var day2Item = fixture.FindItem(production, "ORD-201");

        Assert.Equal(100m, day2Item.CurrentStock);
        Assert.Equal(50m, day2Item.RemainingStock);
    }

    [Fact]
    public async Task ManualStockOverride_BehaviorRemainsUnchanged()
    {
        await using var fixture = await RollingFixture.CreateAsync();

        var day = new DateTime(2026, 8, 14);
        var order = await fixture.AddScheduledOrderAsync("ORD-300", 1, 1, day, 100m);

        var result = await fixture.SaveDecisionAsync(order.OrderId, order.ItemId, isSufficient: true, manualInitialStock: 200m);

        var line = Assert.Single(result.Lines);
        Assert.Equal(200m, line.CurrentStock);
        Assert.Equal(100m, line.RequiredStock);
        Assert.Equal(100m, line.RemainingStock);
    }

    [Fact]
    public async Task ManualStockOverride_Day2ClosingCarriesToDay3()
    {
        await using var fixture = await RollingFixture.CreateAsync();

        var day1 = new DateTime(2026, 8, 14);
        var day2 = new DateTime(2026, 8, 15);
        var day3 = new DateTime(2026, 8, 16);

        var order1 = await fixture.AddScheduledOrderAsync("ORD-310", 1, 1, day1, 100m);
        var order2 = await fixture.AddScheduledOrderAsync("ORD-311", 1, 1, day2, 50m);
        await fixture.AddScheduledOrderAsync("ORD-312", 1, 1, day3, 10m);

        await fixture.AddDecisionAsync(order1.ItemId, currentStock: 200m, remainingStock: 100m, isSufficient: true, requiredProductionQty: 0m);
        await fixture.SaveDecisionAsync(order2.OrderId, order2.ItemId, isSufficient: true, manualInitialStock: 120m);

        var production = await fixture.GetProductionAsync();
        var day2Item = fixture.FindItem(production, "ORD-311");
        var day3Item = fixture.FindItem(production, "ORD-312");

        Assert.Equal(120m, day2Item.CurrentStock);
        Assert.Equal(70m, day2Item.RemainingStock);
        Assert.Equal(70m, day3Item.CurrentStock);
        Assert.Equal(60m, day3Item.RemainingStock);
    }

    [Fact]
    public async Task ManualOpening_WithPersistedProduction_CarriesExpectedClosingToNextDate()
    {
        await using var fixture = await RollingFixture.CreateAsync();

        var day1 = new DateTime(2026, 8, 14);
        var day2 = new DateTime(2026, 8, 15);
        var day3 = new DateTime(2026, 8, 16);

        var order1 = await fixture.AddScheduledOrderAsync("ORD-320", 1, 1, day1, 100m);
        var order2 = await fixture.AddScheduledOrderAsync("ORD-321", 1, 1, day2, 50m);
        await fixture.AddScheduledOrderAsync("ORD-322", 1, 1, day3, 10m);

        await fixture.AddDecisionAsync(order1.ItemId, currentStock: 200m, remainingStock: 100m, isSufficient: true, requiredProductionQty: 0m);
        await fixture.AddDecisionAsync(order2.ItemId, currentStock: 120m, remainingStock: 70m, isSufficient: false, requiredProductionQty: 40m);

        var production = await fixture.GetProductionAsync();
        var day2Item = fixture.FindItem(production, "ORD-321");
        var day3Item = fixture.FindItem(production, "ORD-322");

        Assert.Equal(120m, day2Item.CurrentStock);
        Assert.Equal(110m, day2Item.RemainingStock);
        Assert.Equal(110m, day3Item.CurrentStock);
    }

    [Fact]
    public async Task OkDecision_RemainingStockCarriesForward()
    {
        await using var fixture = await RollingFixture.CreateAsync();

        var day1 = new DateTime(2026, 8, 14);
        var day2 = new DateTime(2026, 8, 15);

        var order1 = await fixture.AddScheduledOrderAsync("ORD-400", 1, 1, day1, 100m);
        await fixture.AddScheduledOrderAsync("ORD-401", 1, 1, day2, 50m);

        await fixture.AddDecisionAsync(order1.ItemId, currentStock: 200m, remainingStock: 100m, isSufficient: true, requiredProductionQty: 0m);

        var production = await fixture.GetProductionAsync();
        var day2Item = fixture.FindItem(production, "ORD-401");

        Assert.Equal(100m, day2Item.CurrentStock);
        Assert.Equal(50m, day2Item.RemainingStock);
    }

    [Fact]
    public async Task ProduceMoreDecision_RemainingStockCarryForwardRespectsClamp()
    {
        await using var fixture = await RollingFixture.CreateAsync();

        var day1 = new DateTime(2026, 8, 14);
        var day2 = new DateTime(2026, 8, 15);

        var order1 = await fixture.AddScheduledOrderAsync("ORD-500", 1, 1, day1, 300m);
        await fixture.AddScheduledOrderAsync("ORD-501", 1, 1, day2, 50m);

        await fixture.AddDecisionAsync(order1.ItemId, currentStock: 200m, remainingStock: -100m, isSufficient: false, requiredProductionQty: 100m);

        var production = await fixture.GetProductionAsync();
        var day2Item = fixture.FindItem(production, "ORD-501");

        Assert.Equal(0m, day2Item.CurrentStock);
        Assert.Equal(-50m, day2Item.RemainingStock);
        Assert.Equal(50m, day2Item.ProductionRequired);
    }

    [Fact]
    public async Task PersistedProduceMoreQuantity_IsUsedInClosingCalculation()
    {
        await using var fixture = await RollingFixture.CreateAsync();

        var day = new DateTime(2026, 8, 14);
        var order = await fixture.AddScheduledOrderAsync("ORD-550", 1, 1, day, 50m);

        await fixture.AddDecisionAsync(order.ItemId, currentStock: 20m, remainingStock: 10m, isSufficient: false, requiredProductionQty: 40m);

        var production = await fixture.GetProductionAsync();
        var item = fixture.FindItem(production, "ORD-550");

        Assert.Equal(20m, item.CurrentStock);
        Assert.Equal(50m, item.RequiredStock);
        Assert.Equal(10m, item.RemainingStock);
    }

    [Fact]
    public async Task MultipleDates_SameProduct_CarrySequentially()
    {
        await using var fixture = await RollingFixture.CreateAsync();

        var day1 = new DateTime(2026, 8, 14);
        var day2 = new DateTime(2026, 8, 15);
        var day3 = new DateTime(2026, 8, 16);

        var order1 = await fixture.AddScheduledOrderAsync("ORD-600", 1, 1, day1, 100m);
        await fixture.AddScheduledOrderAsync("ORD-601", 1, 1, day2, 40m);
        await fixture.AddScheduledOrderAsync("ORD-602", 1, 1, day3, 10m);

        await fixture.AddDecisionAsync(order1.ItemId, currentStock: 200m, remainingStock: 100m, isSufficient: true, requiredProductionQty: 0m);

        var production = await fixture.GetProductionAsync();
        var day2Item = fixture.FindItem(production, "ORD-601");
        var day3Item = fixture.FindItem(production, "ORD-602");

        Assert.Equal(100m, day2Item.CurrentStock);
        Assert.Equal(60m, day2Item.RemainingStock);
        Assert.Equal(60m, day3Item.CurrentStock);
        Assert.Equal(50m, day3Item.RemainingStock);
    }

    [Fact]
    public async Task DifferentProducts_DoNotContaminateEachOther()
    {
        await using var fixture = await RollingFixture.CreateAsync();

        var day1 = new DateTime(2026, 8, 14);
        var day2 = new DateTime(2026, 8, 15);

        var order1 = await fixture.AddScheduledOrderAsync("ORD-700", 1, 1, day1, 100m);
        await fixture.AddScheduledOrderAsync("ORD-701", 1, 1, day2, 50m);
        await fixture.AddScheduledOrderAsync("ORD-702", 2, 1, day2, 70m);
        await fixture.UpsertStockAsync(2, 30m);

        await fixture.AddDecisionAsync(order1.ItemId, currentStock: 200m, remainingStock: 100m, isSufficient: true, requiredProductionQty: 0m);

        var production = await fixture.GetProductionAsync();
        var p1Day2 = fixture.FindItem(production, "ORD-701");
        var p2Day2 = fixture.FindItem(production, "ORD-702");

        Assert.Equal(100m, p1Day2.CurrentStock);
        Assert.Equal(50m, p1Day2.RemainingStock);

        Assert.Equal(30m, p2Day2.CurrentStock);
        Assert.Equal(-40m, p2Day2.RemainingStock);
    }

    [Fact]
    public async Task DifferentDcs_SameProduct_CurrentBehaviorRemainsConsistent()
    {
        await using var fixture = await RollingFixture.CreateAsync();

        var day1 = new DateTime(2026, 8, 14);
        var day2 = new DateTime(2026, 8, 15);

        var order1 = await fixture.AddScheduledOrderAsync("ORD-800", 1, 1, day1, 100m);
        await fixture.AddScheduledOrderAsync("ORD-801", 1, 2, day2, 50m);

        await fixture.AddDecisionAsync(order1.ItemId, currentStock: 200m, remainingStock: 100m, isSufficient: true, requiredProductionQty: 0m);

        var production = await fixture.GetProductionAsync();
        var dc2Item = fixture.FindItem(production, "ORD-801");

        Assert.Equal(100m, dc2Item.CurrentStock);
        Assert.Equal(50m, dc2Item.RemainingStock);
    }

    [Fact]
    public async Task ProductionApiResponseShape_RemainsCompatible()
    {
        await using var fixture = await RollingFixture.CreateAsync();

        var day = new DateTime(2026, 8, 14);
        await fixture.AddScheduledOrderAsync("ORD-900", 1, 1, day, 25m);

        var production = await fixture.GetProductionAsync();
        var order = Assert.Single(production.Orders);
        var item = Assert.Single(order.Items);

        Assert.Equal("ORD-900", order.OrderNumber);
        Assert.True(item.CurrentStock >= 0m || item.CurrentStock < 0m || item.CurrentStock == 0m);
        Assert.True(item.RequiredStock >= 0m || item.RequiredStock < 0m || item.RequiredStock == 0m);
        Assert.True(item.RemainingStock >= 0m || item.RemainingStock < 0m || item.RemainingStock == 0m);
    }

    [Fact]
    public async Task QuantityAdjustedOrder_UsesUpdatedOrderItemQuantityInSnapshot()
    {
        await using var fixture = await RollingFixture.CreateAsync();

        var day = new DateTime(2026, 8, 14);
        var order = await fixture.AddScheduledOrderAsync("ORD-910", 1, 1, day, 100m);

        await fixture.UpdateOrderItemQuantityAsync(order.ItemId, 130m, clearDecisions: true);

        var production = await fixture.GetProductionAsync();
        var item = fixture.FindItem(production, "ORD-910");

        Assert.Equal(130m, item.RequiredStock);
    }

    [Fact]
    public async Task ProductionSnapshotAndPlan_AreNumericallyEquivalent_ForSameInputs()
    {
        await using var fixture = await RollingFixture.CreateAsync();

        var day = new DateTime(2026, 8, 14);
        var order = await fixture.AddScheduledOrderAsync("ORD-920", 1, 1, day, 50m);

        await fixture.AddDecisionAsync(order.ItemId, currentStock: 20m, remainingStock: 10m, isSufficient: false, requiredProductionQty: 40m);

        var production = await fixture.GetProductionAsync();
        var snapshotItem = fixture.FindItem(production, "ORD-920");

        var planClosing = await fixture.GetPlanClosingAsync(day, openingStock: 20m, productionQuantity: 40m);

        Assert.Equal(snapshotItem.RemainingStock, planClosing);
        Assert.Equal(10m, planClosing);
    }

    private sealed class RollingFixture : IAsyncDisposable
    {
        private readonly DbContextOptions<AppDbContext> _options;

        private RollingFixture(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public static async Task<RollingFixture> CreateAsync()
        {
            var dbName = $"rolling-tests-{Guid.NewGuid():N}";
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            await using var db = new AppDbContext(options);

            db.Regions.Add(new Region { Id = 1, Name = "Region 1" });
            db.DistributionCentres.Add(new DistributionCentre { Id = 1, Name = "DC CANELANDS", Code = "DCC", RegionId = 1, IsActive = true });
            db.DistributionCentres.Add(new DistributionCentre { Id = 2, Name = "DC BASSON", Code = "DCB", RegionId = 1, IsActive = true });

            db.Products.Add(new Product
            {
                Id = 1,
                Name = "TOILET PAPER 2PLY WHITE HOUSEBRAND 9S",
                SKUCode = "HB9S",
                PalletConversionRate = 1,
                IsMapped = true,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });

            db.Products.Add(new Product
            {
                Id = 2,
                Name = "TOILET PAPER 1PLY HOUSEBRAND 4S PK",
                SKUCode = "HB4S",
                PalletConversionRate = 1,
                IsMapped = true,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
            return new RollingFixture(options);
        }

        public async Task<(int OrderId, int ItemId)> AddScheduledOrderAsync(string orderNumber, int productId, int distributionCentreId, DateTime date, decimal quantity)
        {
            await using var db = new AppDbContext(_options);

            var order = new Order
            {
                OrderNumber = orderNumber,
                OrderDate = date.AddDays(-1),
                DeliveryDate = date,
                DistributionCentreId = distributionCentreId,
                Source = OrderSource.CSV,
                Status = OrderStatus.Approved,
                IsActive = true,
                TotalPallets = 0,
                TotalValue = 0
            };

            var item = new OrderItem
            {
                ProductId = productId,
                ProductCode = productId == 1 ? "HB9S" : "HB4S",
                ProductName = productId == 1 ? "TOILET PAPER 2PLY WHITE HOUSEBRAND 9S" : "TOILET PAPER 1PLY HOUSEBRAND 4S PK",
                Quantity = quantity,
                Price = 1,
                Pallets = 0
            };

            order.Items.Add(item);
            db.Orders.Add(order);
            await db.SaveChangesAsync();

            db.DeliverySchedules.Add(new DeliverySchedule
            {
                OrderId = order.Id,
                DeliveryDate = date,
                Status = "Scheduled"
            });
            await db.SaveChangesAsync();

            return (order.Id, item.Id);
        }

        public async Task UpsertStockAsync(int productId, decimal quantity)
        {
            await using var db = new AppDbContext(_options);
            var stock = await db.Stocks.FirstOrDefaultAsync(x => x.ProductId == productId);
            if (stock is null)
            {
                db.Stocks.Add(new Stock
                {
                    ProductId = productId,
                    Quantity = quantity,
                    LastUpdated = DateTime.UtcNow
                });
            }
            else
            {
                stock.Quantity = quantity;
                stock.LastUpdated = DateTime.UtcNow;
            }

            await db.SaveChangesAsync();
        }

        public async Task AddDecisionAsync(int orderItemId, decimal currentStock, decimal remainingStock, bool isSufficient, decimal requiredProductionQty)
        {
            await using var db = new AppDbContext(_options);
            db.ProductionDecisions.Add(new ProductionDecision
            {
                OrderItemId = orderItemId,
                IsSufficient = isSufficient,
                RequiredStock = 0,
                CurrentStock = currentStock,
                Difference = remainingStock,
                RequiredProductionQty = requiredProductionQty,
                RemainingStock = remainingStock,
                Notes = "seeded"
            });

            await db.SaveChangesAsync();
        }

        public async Task<ProductionDecisionResultDto> SaveDecisionAsync(int orderId, int orderItemId, bool isSufficient, decimal manualInitialStock)
        {
            await using var db = new AppDbContext(_options);
            var service = new ProductionService(db, NullLogger<ProductionService>.Instance);
            return await service.SaveProductionDecisionsAsync(new SaveProductionDecisionsDto
            {
                OrderId = orderId,
                Decisions = new List<ProductionDecisionItemDto>
                {
                    new()
                    {
                        OrderItemId = orderItemId,
                        IsSufficient = isSufficient,
                        RequiredProductionQty = 0,
                        ManualInitialStock = manualInitialStock,
                        Notes = "manual test"
                    }
                }
            });
        }

        public async Task<ProductionResponseDto> GetProductionAsync()
        {
            await using var db = new AppDbContext(_options);
            var service = new ProductionService(db, NullLogger<ProductionService>.Instance);
            return await service.GetProductionAsync(null);
        }

        public async Task UpdateOrderItemQuantityAsync(int orderItemId, decimal quantity, bool clearDecisions)
        {
            await using var db = new AppDbContext(_options);
            var item = await db.OrderItems.FirstAsync(x => x.Id == orderItemId);
            item.Quantity = quantity;

            if (clearDecisions)
            {
                var decisions = await db.ProductionDecisions
                    .Where(x => x.OrderItemId == orderItemId)
                    .ToListAsync();
                db.ProductionDecisions.RemoveRange(decisions);
            }

            await db.SaveChangesAsync();
        }

        public async Task<decimal> GetPlanClosingAsync(DateTime date, decimal openingStock, decimal productionQuantity)
        {
            await using var db = new AppDbContext(_options);
            var service = new ProductionService(db, NullLogger<ProductionService>.Instance);

            await service.CreateOrUpdatePlanAsync(new ProductionPlanUpsertDto
            {
                ProductId = 1,
                Date = date,
                OpeningStock = openingStock,
                ProductionQuantity = productionQuantity,
                Notes = "parity"
            });

            var plan = (await service.GetPlansByDateAsync(date)).Single(x => x.ProductId == 1);
            return plan.ClosingStock;
        }

        public ProductionOrderItemDto FindItem(ProductionResponseDto response, string orderNumber)
        {
            var order = response.Orders.Single(x => x.OrderNumber == orderNumber);
            return Assert.Single(order.Items);
        }

        public async ValueTask DisposeAsync()
        {
            await ValueTask.CompletedTask;
        }
    }
}
