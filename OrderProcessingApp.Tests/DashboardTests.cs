using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;
using OrderProcessingApp.Services;
using Xunit;

namespace OrderProcessingApp.Tests;

public class DashboardTests
{
    [Fact]
    public async Task Dashboard_ExcludesInactiveOrdersAndUsesExplicitAssignmentAndSchedule()
    {
        await using var fixture = await DashboardFixture.CreateAsync();
        var assignedScheduled = await fixture.AddOrderAsync("ASSIGNED", fixture.North, isAssigned: true, isScheduled: true);
        await fixture.AddOrderAsync("UNASSIGNED-DATED", fixture.North, deliveryDate: DateTime.UtcNow.Date.AddDays(1));
        await fixture.AddOrderAsync("INACTIVE", fixture.North, isActive: false);

        var result = await fixture.Service.GetDashboardAsync(new DashboardFilterDto());

        Assert.Equal(2, result.TotalOrders);
        Assert.Equal(1, result.AssignedOrders);
        Assert.Equal(1, result.UnassignedOrders);
        Assert.Equal(1, result.ScheduledOrders);
        Assert.Equal(1, result.UnscheduledOrders);
        Assert.Contains(result.Orders, order => order.OrderNumber == assignedScheduled.OrderNumber && order.IsAssignedToProduction && order.IsScheduled);
        Assert.Contains(result.Orders, order => order.OrderNumber == "UNASSIGNED-DATED" && !order.IsAssignedToProduction && !order.IsScheduled);
        Assert.DoesNotContain(result.Orders, order => order.OrderNumber == "INACTIVE");
    }

    [Fact]
    public async Task Dashboard_AppliesMultipleCentresAndCombinedFiltersBeforeAggregating()
    {
        await using var fixture = await DashboardFixture.CreateAsync();
        await fixture.AddOrderAsync("NORTH-ASSIGNED", fixture.North, isAssigned: true, isScheduled: true);
        await fixture.AddOrderAsync("SOUTH-ASSIGNED", fixture.South, isAssigned: true, isScheduled: true);
        await fixture.AddOrderAsync("NORTH-UNASSIGNED", fixture.North);
        await fixture.AddOrderAsync("OTHER", fixture.Other, isAssigned: true, isScheduled: true);

        var result = await fixture.Service.GetDashboardAsync(new DashboardFilterDto
        {
            DistributionCentreIds = new List<int> { fixture.North.Id, fixture.South.Id },
            ProductionAssignment = "assigned",
            DeliveryStatus = "scheduled"
        });

        Assert.Equal(2, result.TotalOrders);
        Assert.Equal(2, result.AssignedOrders);
        Assert.Equal(2, result.ScheduledOrders);
        Assert.Equal(0, result.UnassignedOrders);
        Assert.Equal(2, result.DistributionCentres.Sum(row => row.TotalOrders));
        Assert.Equal(result.TotalOrderValue, result.Orders.Sum(order => order.TotalValue));
        Assert.DoesNotContain(result.Orders, order => order.OrderNumber == "OTHER");
        Assert.DoesNotContain(result.Orders, order => order.OrderNumber == "NORTH-UNASSIGNED");
    }

    [Fact]
    public async Task Dashboard_ExceptionFilterAndProductAggregationUseTheFilteredDataset()
    {
        await using var fixture = await DashboardFixture.CreateAsync();
        await fixture.AddOrderAsync("FLAGGED", fixture.North, status: OrderStatus.Flagged, priceMismatch: true);
        await fixture.AddOrderAsync("VALID", fixture.North, status: OrderStatus.Approved);

        var result = await fixture.Service.GetDashboardAsync(new DashboardFilterDto { Exception = "pricing issue" });

        Assert.Equal(1, result.TotalOrders);
        Assert.Equal("FLAGGED", Assert.Single(result.Orders).OrderNumber);
        Assert.Equal(result.TotalOrderValue, result.Products.Sum(product => product.TotalRevenue));
        Assert.Contains(result.RequiresAttention, row => row.OrderNumber == "FLAGGED" && row.Exception == "Pricing Issue");
    }

    private sealed class DashboardFixture : IAsyncDisposable
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public DistributionCentre North { get; private set; } = null!;
        public DistributionCentre South { get; private set; } = null!;
        public DistributionCentre Other { get; private set; } = null!;
        public ReportService Service => new(CreateDbContext());

        private DashboardFixture(DbContextOptions<AppDbContext> options) => _options = options;

        public static async Task<DashboardFixture> CreateAsync()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"dashboard-{Guid.NewGuid():N}")
                .Options;
            var fixture = new DashboardFixture(options);
            await using var db = fixture.CreateDbContext();
            var region = new Region { Name = "Dashboard Region" };
            fixture.North = new DistributionCentre { Name = "North", Code = "NORTH", Region = region };
            fixture.South = new DistributionCentre { Name = "South", Code = "SOUTH", Region = region };
            fixture.Other = new DistributionCentre { Name = "Other", Code = "OTHER", Region = region };
            db.Regions.Add(region);
            db.DistributionCentres.AddRange(fixture.North, fixture.South, fixture.Other);
            await db.SaveChangesAsync();
            return fixture;
        }

        public async Task<Order> AddOrderAsync(
            string number,
            DistributionCentre centre,
            OrderStatus status = OrderStatus.Approved,
            bool isAssigned = false,
            bool isScheduled = false,
            bool isActive = true,
            DateTime? deliveryDate = null,
            bool priceMismatch = false)
        {
            await using var db = CreateDbContext();
            var product = new Product { Name = "Test Product", SKUCode = $"SKU-{number}" };
            var order = new Order
            {
                OrderNumber = number,
                OrderDate = DateTime.UtcNow.Date.AddDays(-1),
                DeliveryDate = deliveryDate,
                DistributionCentreId = centre.Id,
                Status = status,
                IsAssignedToProduction = isAssigned,
                IsActive = isActive,
                TotalValue = 20,
                TotalPallets = 1,
                Items = new List<OrderItem>
                {
                    new() { Product = product, ProductCode = product.SKUCode, ProductName = product.Name, Quantity = 2, Price = 10, Pallets = 1, IsPriceMismatch = priceMismatch }
                }
            };
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            if (isScheduled)
            {
                db.DeliverySchedules.Add(new DeliverySchedule { OrderId = order.Id, DeliveryDate = deliveryDate ?? DateTime.UtcNow.Date.AddDays(1) });
                await db.SaveChangesAsync();
            }
            return order;
        }

        public AppDbContext CreateDbContext() => new(_options);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
