using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;
using OrderProcessingApp.Services;
using Xunit;

namespace OrderProcessingApp.Tests;

public class OrdersListPricingRefreshTests
{
    [Fact]
    public async Task GetFilteredOrdersAsync_UsesCurrentPriceListInsteadOfPersistedFlags()
    {
        await using var fixture = await TestFixture.CreateAsync();

        var product = await fixture.CreateProductAsync("Product 1", "SKU-1");
        var dc = await fixture.CreateDistributionCentreAsync("DC North");
        await fixture.CreateOrderAsync(product.Id, dc.Id, price: 12m, staleMissing: true, staleMismatch: true);
        await fixture.CreatePriceListAsync(product.Id, dc.Id, 12m);

        var service = fixture.CreateOrderService();
        var orders = await service.GetFilteredOrdersAsync(new OrderFilterDto());

        var order = Assert.Single(orders);
        var item = Assert.Single(order.Items);
        Assert.False(item.IsPriceMissing);
        Assert.False(item.IsPriceMismatch);
    }

    [Fact]
    public async Task GetFilteredOrdersAsync_ReportsMismatch_WhenCurrentSystemPriceDiffers()
    {
        await using var fixture = await TestFixture.CreateAsync();

        var product = await fixture.CreateProductAsync("Product 1", "SKU-1");
        var dc = await fixture.CreateDistributionCentreAsync("DC North");
        await fixture.CreateOrderAsync(product.Id, dc.Id, price: 12m, staleMissing: false, staleMismatch: true);
        await fixture.CreatePriceListAsync(product.Id, dc.Id, 15m);

        var service = fixture.CreateOrderService();
        var orders = await service.GetFilteredOrdersAsync(new OrderFilterDto());

        var order = Assert.Single(orders);
        var item = Assert.Single(order.Items);
        Assert.False(item.IsPriceMissing);
        Assert.True(item.IsPriceMismatch);
    }

    [Fact]
    public async Task GetFilteredOrdersAsync_UsesOrderDistributionCentreForLookup()
    {
        await using var fixture = await TestFixture.CreateAsync();

        var product = await fixture.CreateProductAsync("Product A", "SKU-A");
        var north = await fixture.CreateDistributionCentreAsync("DC North");
        var south = await fixture.CreateDistributionCentreAsync("DC South");

        await fixture.CreatePriceListAsync(product.Id, north.Id, 10m);
        await fixture.CreatePriceListAsync(product.Id, south.Id, 12m);

        await fixture.CreateOrderAsync(product.Id, north.Id, price: 10m, staleMissing: true, staleMismatch: true, orderNumber: "ORD-NORTH");
        await fixture.CreateOrderAsync(product.Id, south.Id, price: 10m, staleMissing: true, staleMismatch: true, orderNumber: "ORD-SOUTH");

        var service = fixture.CreateOrderService();
        var orders = await service.GetFilteredOrdersAsync(new OrderFilterDto());

        var northOrder = Assert.Single(orders, x => x.OrderNumber == "ORD-NORTH");
        var southOrder = Assert.Single(orders, x => x.OrderNumber == "ORD-SOUTH");

        var northItem = Assert.Single(northOrder.Items);
        var southItem = Assert.Single(southOrder.Items);

        Assert.False(northItem.IsPriceMissing);
        Assert.False(northItem.IsPriceMismatch);
        Assert.False(southItem.IsPriceMissing);
        Assert.True(southItem.IsPriceMismatch);
    }

    [Fact]
    public async Task PricingService_GetEffectivePricesAsync_ResolvesDistinctProductDcPairsOnce()
    {
        await using var fixture = await TestFixture.CreateAsync();

        var productA = await fixture.CreateProductAsync("Product A", "SKU-A");
        var productB = await fixture.CreateProductAsync("Product B", "SKU-B");
        var north = await fixture.CreateDistributionCentreAsync("DC North");
        var south = await fixture.CreateDistributionCentreAsync("DC South");

        await fixture.CreatePriceListAsync(productA.Id, north.Id, 10m);
        await fixture.CreatePriceListAsync(productA.Id, south.Id, 12m);
        await fixture.CreatePriceListAsync(productB.Id, north.Id, 14m);

        await using var db = new AppDbContext(fixture.Options);
        var pricingService = new PricingService(db, NullLogger<PricingService>.Instance);

        var results = await pricingService.GetEffectivePricesAsync(new[]
        {
            (productA.Id, north.Id),
            (productA.Id, south.Id),
            (productB.Id, north.Id),
            (productA.Id, north.Id)
        });

        Assert.Equal(3, results.Count);
        Assert.Equal(10m, results[(productA.Id, north.Id)].EffectivePrice);
        Assert.Equal(12m, results[(productA.Id, south.Id)].EffectivePrice);
        Assert.Equal(14m, results[(productB.Id, north.Id)].EffectivePrice);
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        public DbContextOptions<AppDbContext> Options { get; }

        private TestFixture(DbContextOptions<AppDbContext> options)
        {
            Options = options;
        }

        public static async Task<TestFixture> CreateAsync()
        {
            var dbName = $"orders-list-pricing-tests-{Guid.NewGuid():N}";
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(dbName)
                .Options;

            await using (var db = new AppDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
            }

            return new TestFixture(options);
        }

        public async Task<Product> CreateProductAsync(string name, string sku)
        {
            await using var db = new AppDbContext(Options);
            var product = new Product
            {
                Name = name,
                SKUCode = sku,
                PalletConversionRate = 1m,
                IsMapped = true,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            db.Products.Add(product);
            await db.SaveChangesAsync();
            return product;
        }

        public async Task<DistributionCentre> CreateDistributionCentreAsync(string name)
        {
            await using var db = new AppDbContext(Options);

            var region = await db.Regions.FirstOrDefaultAsync() ?? new Region { Name = "Region 1" };
            if (region.Id == 0)
            {
                db.Regions.Add(region);
                await db.SaveChangesAsync();
            }

            var centre = new DistributionCentre
            {
                Name = name,
                Code = name.Replace(" ", "-"),
                RegionId = region.Id,
                IsActive = true,
                RequiresAttention = false
            };

            db.DistributionCentres.Add(centre);
            await db.SaveChangesAsync();
            return centre;
        }

        public async Task CreatePriceListAsync(int productId, int distributionCentreId, decimal price)
        {
            await using var db = new AppDbContext(Options);
            var entry = new PriceList
            {
                ProductId = productId,
                DistributionCentreId = distributionCentreId,
                Price = price,
                IsActive = true
            };

            db.PriceLists.Add(entry);
            await db.SaveChangesAsync();
        }

        public async Task CreateOrderAsync(int productId, int distributionCentreId, decimal price, bool staleMissing, bool staleMismatch, string orderNumber = "ORD-1")
        {
            await using var db = new AppDbContext(Options);

            var order = new Order
            {
                OrderNumber = orderNumber,
                OrderDate = new DateTime(2026, 8, 1),
                DeliveryDate = new DateTime(2026, 8, 5),
                DistributionCentreId = distributionCentreId,
                Source = OrderSource.CSV,
                Status = OrderStatus.Flagged,
                IsActive = true,
                TotalValue = price * 30m,
                TotalPallets = 0m
            };

            order.Items.Add(new OrderItem
            {
                ProductId = productId,
                ProductCode = "SKU-X",
                ProductName = "Product X",
                Quantity = 30m,
                Price = price,
                Pallets = 0m,
                IsUnmapped = false,
                IsPriceMissing = staleMissing,
                IsPriceMismatch = staleMismatch,
                IsCsvPrice = true,
                MetadataJson = "{}"
            });

            db.Orders.Add(order);
            await db.SaveChangesAsync();
        }

        public OrderService CreateOrderService()
        {
            var db = new AppDbContext(Options);
            return new OrderService(
                db,
                new PricingService(db, NullLogger<PricingService>.Instance),
                new StubPalletService(),
                new StubPlanningService(),
                new StubDistributionCentreResolver(),
                new StubStockService(),
                new StubAuditService(),
                NullLogger<OrderService>.Instance);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        private sealed class StubPalletService : IPalletService
        {
            public Task<decimal> CalculatePalletsAsync(int productId, decimal quantity, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(quantity);
            }
        }

        private sealed class StubPlanningService : IPlanningService
        {
            public Task<PlanningCheckResult> CheckStockVsProductionRequirementsAsync(int productId, decimal requiredQuantity, DateTime deliveryDate, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new PlanningCheckResult { IsSufficient = true, AvailableQuantity = requiredQuantity, RequiredQuantity = requiredQuantity, Shortfall = 0m });
            }
        }

        private sealed class StubDistributionCentreResolver : IDistributionCentreResolver
        {
            public Task<DistributionCentreResolutionResult> ResolveFromCsvAsync(string rawDistributionCentre, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException("Not used for list pricing tests.");
            }
        }

        private sealed class StubStockService : IStockService
        {
            public Task<Dictionary<int, Stock>> GetByProductIdsAsync(IEnumerable<int> productIds, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new Dictionary<int, Stock>());
            }

            public Task<StockDto> UpdateManualStockAsync(StockUpdateDto dto, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException("Not used for list pricing tests.");
            }
        }

        private sealed class StubAuditService : IAuditService
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
}
