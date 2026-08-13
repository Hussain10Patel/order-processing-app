using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;
using OrderProcessingApp.Models;
using OrderProcessingApp.Services;
using Xunit;

namespace OrderProcessingApp.Tests;

public class ReportRangeTests
{
    [Fact]
    public async Task GetSummaryByDeliveryDateAsync_SingleDayAndSameDayRangeMatch()
    {
        await using var fixture = await TestFixture.CreateAsync();

        var product = await fixture.CreateProductAsync("Widget", "W-100");
        var dc = await fixture.CreateDistributionCentreAsync("North DC");
        var order = await fixture.CreateOrderAsync(
            "ORD-001",
            new DateTime(2026, 8, 13),
            new DateTime(2026, 8, 13),
            dc.Id,
            OrderStatus.Approved,
            new[]
            {
                new OrderItem { ProductId = product.Id, ProductCode = product.SKUCode, ProductName = product.Name, Quantity = 2m, Price = 25m, Pallets = 1m }
            });

        var service = fixture.CreateReportService();

        var singleDay = await service.GetSummaryByDeliveryDateAsync(new DateTime(2026, 8, 13));
        var sameDayRange = await service.GetSummaryByDeliveryDateRangeAsync(new DateTime(2026, 8, 13), new DateTime(2026, 8, 13));

        Assert.Equal(singleDay.TotalOrders, sameDayRange.TotalOrders);
        Assert.Equal(singleDay.TotalValue, sameDayRange.TotalValue);
        Assert.Equal(singleDay.OrdersByStatus.Count, sameDayRange.OrdersByStatus.Count);
        Assert.Equal(singleDay.SalesByProduct.Count, sameDayRange.SalesByProduct.Count);
        Assert.Equal(singleDay.DeliverySummary.Count, sameDayRange.DeliverySummary.Count);
    }

    [Fact]
    public async Task GetSummaryByDeliveryDateRangeAsync_IncludesAllOrdersWithinRangeAndExcludesOutside()
    {
        await using var fixture = await TestFixture.CreateAsync();

        var product = await fixture.CreateProductAsync("Widget", "W-100");
        var dc = await fixture.CreateDistributionCentreAsync("North DC");

        await fixture.CreateOrderAsync("ORD-OUT-BEFORE", new DateTime(2026, 8, 10), new DateTime(2026, 8, 10), dc.Id, OrderStatus.Pending,
            new[] { new OrderItem { ProductId = product.Id, ProductCode = product.SKUCode, ProductName = product.Name, Quantity = 1m, Price = 10m, Pallets = 1m } });

        await fixture.CreateOrderAsync("ORD-IN-1", new DateTime(2026, 8, 11), new DateTime(2026, 8, 11), dc.Id, OrderStatus.Approved,
            new[] { new OrderItem { ProductId = product.Id, ProductCode = product.SKUCode, ProductName = product.Name, Quantity = 2m, Price = 20m, Pallets = 1m } });

        await fixture.CreateOrderAsync("ORD-IN-2", new DateTime(2026, 8, 13), new DateTime(2026, 8, 13), dc.Id, OrderStatus.Processed,
            new[] { new OrderItem { ProductId = product.Id, ProductCode = product.SKUCode, ProductName = product.Name, Quantity = 3m, Price = 30m, Pallets = 1m } });

        await fixture.CreateOrderAsync("ORD-OUT-AFTER", new DateTime(2026, 8, 14), new DateTime(2026, 8, 14), dc.Id, OrderStatus.Pending,
            new[] { new OrderItem { ProductId = product.Id, ProductCode = product.SKUCode, ProductName = product.Name, Quantity = 4m, Price = 40m, Pallets = 1m } });

        var service = fixture.CreateReportService();
        var summary = await service.GetSummaryByDeliveryDateRangeAsync(new DateTime(2026, 8, 11), new DateTime(2026, 8, 13));

        Assert.Equal(2, summary.TotalOrders);
        Assert.Equal(2m * 20m + 3m * 30m, summary.TotalValue);
        Assert.Equal(2, summary.DeliverySummary.Count);
        Assert.Contains(summary.DeliverySummary, row => row.PoNumber == "ORD-IN-1" && row.DeliveryDate == "2026-08-11");
        Assert.Contains(summary.DeliverySummary, row => row.PoNumber == "ORD-IN-2" && row.DeliveryDate == "2026-08-13");
        Assert.DoesNotContain(summary.DeliverySummary, row => row.PoNumber == "ORD-OUT-BEFORE");
        Assert.DoesNotContain(summary.DeliverySummary, row => row.PoNumber == "ORD-OUT-AFTER");
    }

    [Fact]
    public async Task GetSummaryByDeliveryDateRangeAsync_RejectsInvalidRange()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var service = fixture.CreateReportService();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.GetSummaryByDeliveryDateRangeAsync(new DateTime(2026, 8, 20), new DateTime(2026, 8, 10)));
    }

    [Fact]
    public async Task GetSummaryByDeliveryDateRangeAsync_OrdersByStatus_UsesComputedStatus()
    {
        await using var fixture = await TestFixture.CreateAsync();

        var product = await fixture.CreateProductAsync("Widget", "W-100");
        var dc = await fixture.CreateDistributionCentreAsync("North DC");

        await fixture.CreateOrderAsync("ORD-SCHEDULED", new DateTime(2026, 8, 12), new DateTime(2026, 8, 12), dc.Id, OrderStatus.Scheduled,
            new[] { new OrderItem { ProductId = product.Id, ProductCode = product.SKUCode, ProductName = product.Name, Quantity = 1m, Price = 15m, Pallets = 1m } });

        await fixture.CreateOrderAsync("ORD-APPROVED", new DateTime(2026, 8, 12), new DateTime(2026, 8, 12), dc.Id, OrderStatus.Approved,
            new[] { new OrderItem { ProductId = product.Id, ProductCode = product.SKUCode, ProductName = product.Name, Quantity = 1m, Price = 25m, Pallets = 1m } });

        var service = fixture.CreateReportService();
        var summary = await service.GetSummaryByDeliveryDateRangeAsync(new DateTime(2026, 8, 12), new DateTime(2026, 8, 12));

        Assert.Equal(2, summary.TotalOrders);
        Assert.Contains(summary.OrdersByStatus, row => row.Status == "Scheduled" && row.Count == 1);
        Assert.Contains(summary.OrdersByStatus, row => row.Status == "Approved" && row.Count == 1);
    }

    [Fact]
    public async Task GetSummaryByDeliveryDateRangeAsync_SalesByProduct_AggregatesAcrossRange()
    {
        await using var fixture = await TestFixture.CreateAsync();

        var product = await fixture.CreateProductAsync("Widget", "W-100");
        var dc = await fixture.CreateDistributionCentreAsync("North DC");

        await fixture.CreateOrderAsync("ORD-A", new DateTime(2026, 8, 11), new DateTime(2026, 8, 11), dc.Id, OrderStatus.Approved,
            new[] { new OrderItem { ProductId = product.Id, ProductCode = product.SKUCode, ProductName = product.Name, Quantity = 2m, Price = 10m, Pallets = 1m } });

        await fixture.CreateOrderAsync("ORD-B", new DateTime(2026, 8, 13), new DateTime(2026, 8, 13), dc.Id, OrderStatus.Approved,
            new[] { new OrderItem { ProductId = product.Id, ProductCode = product.SKUCode, ProductName = product.Name, Quantity = 3m, Price = 10m, Pallets = 1m } });

        var service = fixture.CreateReportService();
        var summary = await service.GetSummaryByDeliveryDateRangeAsync(new DateTime(2026, 8, 11), new DateTime(2026, 8, 13));

        Assert.Equal(2, summary.TotalOrders);
        var productRow = Assert.Single(summary.SalesByProduct);
        Assert.Equal("Widget", productRow.Product);
        Assert.Equal(5m, productRow.Quantity);
        Assert.Equal(50m, productRow.Value);
    }

    [Fact]
    public async Task GetAvailableReportDatesAsync_IsUnchangedByRangeSupport()
    {
        await using var fixture = await TestFixture.CreateAsync();

        var product = await fixture.CreateProductAsync("Widget", "W-100");
        var dc = await fixture.CreateDistributionCentreAsync("North DC");
        await fixture.CreateOrderAsync("ORD-DATE-1", new DateTime(2026, 8, 12), new DateTime(2026, 8, 12), dc.Id, OrderStatus.Pending,
            new[] { new OrderItem { ProductId = product.Id, ProductCode = product.SKUCode, ProductName = product.Name, Quantity = 1m, Price = 10m, Pallets = 1m } });

        var service = fixture.CreateReportService();
        var dates = await service.GetAvailableReportDatesAsync();

        Assert.Contains(dates, row => row.Date == "2026-08-12");
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
            var dbName = $"report-range-tests-{Guid.NewGuid():N}";
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

        public async Task<Order> CreateOrderAsync(string orderNumber, DateTime orderDate, DateTime deliveryDate, int distributionCentreId, OrderStatus status, IEnumerable<OrderItem> items)
        {
            await using var db = new AppDbContext(Options);
            var order = new Order
            {
                OrderNumber = orderNumber,
                OrderDate = orderDate,
                DeliveryDate = deliveryDate,
                DistributionCentreId = distributionCentreId,
                Status = status,
                TotalValue = items.Sum(item => item.Quantity * item.Price),
                TotalPallets = items.Sum(item => item.Pallets),
                IsActive = true,
                Source = OrderSource.CSV
            };

            db.Orders.Add(order);
            await db.SaveChangesAsync();

            foreach (var item in items)
            {
                item.OrderId = order.Id;
                db.OrderItems.Add(item);
            }

            await db.SaveChangesAsync();
            return order;
        }

        public ReportService CreateReportService()
        {
            return new ReportService(new AppDbContext(Options));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
