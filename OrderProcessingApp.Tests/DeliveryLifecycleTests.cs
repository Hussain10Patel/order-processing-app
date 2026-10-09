using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;
using OrderProcessingApp.Options;
using OrderProcessingApp.Services;
using Xunit;

namespace OrderProcessingApp.Tests;

public class DeliveryLifecycleTests
{
    [Fact]
    public async Task Assignment_PersistsSelectedDateOriginalDateStatusAndSchedule_AcrossRefreshAndPlanner()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.AddOrderAsync(OrderStatus.Approved, assigned: false, scheduled: false);
        await using (var db = fixture.CreateDb())
        {
            var dto = await new ProductionAssignmentService(db).SetDeliveryDateAsync(id, new DateTime(2026, 10, 12));
            Assert.Equal("Scheduled", dto!.Status);
            Assert.Equal("2026-10-10", dto.OriginalCsvDeliveryDate);
        }
        await using var refreshed = fixture.CreateDb();
        var saved = await refreshed.Orders.Include(x => x.DeliverySchedules).SingleAsync(x => x.Id == id);
        Assert.Equal(new DateTime(2026, 10, 10), saved.OriginalCsvDeliveryDate);
        Assert.Equal(new DateTime(2026, 10, 12), saved.DeliveryDate);
        Assert.True(saved.IsAssignedToProduction);
        Assert.Equal(saved.DeliveryDate, Assert.Single(saved.DeliverySchedules).DeliveryDate);
        var planner = new ProductionDeliveryPlannerService(refreshed,
            new ProductionService(refreshed, NullLogger<ProductionService>.Instance), fixture.Clock);
        var plan = await planner.GetCurrentPlanAsync();
        var orderRow = Assert.Single(plan.Events, x => x.OrderId == id);
        Assert.Equal("2026-10-12", orderRow.PlannedDeliveryDate);
        Assert.Equal("Scheduled", orderRow.Status);
        Assert.True(orderRow.CanSetEnRoute);
        var report = await new ReportService(refreshed, fixture.Clock).GetSummaryByDeliveryDateAsync(new DateTime(2026, 10, 12));
        Assert.Equal("2026-10-12", Assert.Single(report.DeliverySummary).DeliveryDate);
        Assert.Empty((await new ReportService(refreshed).GetSummaryByDeliveryDateAsync(new DateTime(2026, 10, 10))).DeliverySummary);
    }

    [Fact]
    public async Task FailedAssignment_RollsBackDateFlagStatusScheduleAndPlannerDate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.AddOrderAsync(OrderStatus.Approved, assigned: false, scheduled: false);
        await using var db = fixture.CreateDb();
        var originalDate = new DateTime(2026, 10, 10);
        var plannerEvent = new ProductionDeliveryPlanEvent
        {
            Plan = new ProductionDeliveryPlan { Name = "Rollback verification" },
            Sequence = 1,
            OrderId = id,
            EventType = ProductionDeliveryPlanEventType.Order,
            PlannedDeliveryDate = originalDate
        };
        db.ProductionDeliveryPlanEvents.Add(plannerEvent);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER reject_schedule BEFORE INSERT ON DeliverySchedules
            BEGIN SELECT RAISE(ABORT, 'Test schedule persistence failure'); END;
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            new ProductionAssignmentService(db).SetDeliveryDateAsync(id, new DateTime(2026, 10, 12)));
        await using var verify = fixture.CreateDb();
        var order = await verify.Orders.SingleAsync();
        Assert.Equal(OrderStatus.Approved, order.Status);
        Assert.False(order.IsAssignedToProduction);
        Assert.Equal(new DateTime(2026, 10, 10), order.DeliveryDate);
        Assert.Empty(await verify.DeliverySchedules.ToListAsync());
        Assert.Equal(originalDate, (await verify.ProductionDeliveryPlanEvents.SingleAsync()).PlannedDeliveryDate);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(7)]
    [InlineData(12)]
    [InlineData(30)]
    [InlineData(72)]
    public async Task EnRoute_PersistsDecimalDurationAndTimer_WithoutResettingOnReopen(double hours)
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.AddOrderAsync();
        var duration = (decimal)hours;
        var started = fixture.Time.GetUtcNow();
        await using (var db = fixture.CreateDb())
        {
            var result = await fixture.Service(db).SetEnRouteAsync(id, duration);
            Assert.Equal("EnRoute", result.Status);
            Assert.Equal(started.AddHours(hours), result.ExpectedDeliveryAtUtc);
        }
        fixture.Time.Now = started.AddMinutes(15);
        await using var reopened = fixture.CreateDb();
        var repeated = await fixture.Service(reopened).SetEnRouteAsync(id, duration);
        Assert.Equal(started, repeated.EnRouteAtUtc);
        Assert.Equal(started.AddHours(hours), repeated.ExpectedDeliveryAtUtc);
        Assert.Equal(duration, repeated.ExpectedDeliveryDurationHours);
        Assert.Equal(0, await fixture.Service(reopened).CompleteDueOrdersAsync());
        Assert.Equal("EnRoute", (await reopened.DeliverySchedules.SingleAsync()).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service(reopened).SetEnRouteAsync(id, duration + 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-1d)]
    public async Task InvalidDuration_IsRejectedWithoutChangingOrder(double? hours)
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.AddOrderAsync();
        await using var db = fixture.CreateDb();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service(db).SetEnRouteAsync(id, hours.HasValue ? (decimal)hours.Value : null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service(db).SetEnRouteAsync(id, decimal.MaxValue));
        await using var verify = fixture.CreateDb();
        var order = await verify.Orders.SingleAsync();
        Assert.Equal(OrderStatus.Scheduled, order.Status);
        Assert.Null(order.EnRouteAtUtc);
        Assert.Null(order.ExpectedDeliveryAtUtc);
    }

    [Fact]
    public async Task Scheduled_DeliversOnlyAfterJohannesburgDayEnds_AndOnlyAssignedScheduledNeverDispatchedOrders()
    {
        await using var fixture = await Fixture.CreateAsync();
        var due = await fixture.AddOrderAsync();
        await fixture.AddOrderAsync(assigned: false);
        await fixture.AddOrderAsync(scheduled: false);
        await fixture.AddOrderAsync(OrderStatus.Approved);
        await fixture.AddOrderAsync(date: new DateTime(2026, 10, 11));
        var previouslyEnRoute = await fixture.AddOrderAsync();
        await using (var db = fixture.CreateDb())
        {
            (await db.Orders.SingleAsync(x => x.Id == previouslyEnRoute)).EnRouteAtUtc = fixture.Time.Now;
            await db.SaveChangesAsync();
        }
        fixture.Time.Now = new DateTimeOffset(2026, 10, 10, 21, 59, 59, TimeSpan.Zero);
        await using (var db = fixture.CreateDb())
        {
            Assert.Equal(0, await fixture.Service(db).CompleteDueOrdersAsync());
        }
        fixture.Time.Now = new DateTimeOffset(2026, 10, 10, 22, 0, 0, TimeSpan.Zero);
        await using (var db = fixture.CreateDb())
        {
            Assert.Equal(1, await fixture.Service(db).CompleteDueOrdersAsync());
            Assert.Equal(0, await fixture.Service(db).CompleteDueOrdersAsync());
        }
        await using var verify = fixture.CreateDb();
        var completed = await verify.Orders.SingleAsync(x => x.Id == due);
        Assert.Equal(OrderStatus.Delivered, completed.Status);
        Assert.True(completed.IsDeliveryEstimated);
        Assert.Equal(fixture.Time.Now, completed.DeliveredAtUtc);
        Assert.Equal(1, await verify.Orders.CountAsync(x => x.Status == OrderStatus.Delivered));
    }

    [Fact]
    public async Task EnRoute_UsesExpectedTimestampNotScheduledDate_AndRepeatedJobDoesNotOverwriteDelivery()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.AddOrderAsync(date: new DateTime(2026, 10, 9));
        var start = fixture.Time.Now;
        await using (var db = fixture.CreateDb())
        {
            await fixture.Service(db).SetEnRouteAsync(id, 30m);
        }
        fixture.Time.Now = start.AddHours(30).AddTicks(-1);
        await using (var db = fixture.CreateDb())
        {
            Assert.Equal(0, await fixture.Service(db).CompleteDueOrdersAsync());
        }
        fixture.Time.Now = start.AddHours(30);
        await using (var db = fixture.CreateDb())
        {
            Assert.Equal(1, await fixture.Service(db).CompleteDueOrdersAsync());
        }
        fixture.Time.Now = start.AddHours(40);
        await using var verify = fixture.CreateDb();
        Assert.Equal(0, await fixture.Service(verify).CompleteDueOrdersAsync());
        var saved = await verify.Orders.SingleAsync();
        Assert.Equal(start.AddHours(30), saved.DeliveredAtUtc);
        Assert.Equal(start, saved.EnRouteAtUtc);
        Assert.Equal("Delivered", (await verify.DeliverySchedules.SingleAsync()).Status);
    }

    [Theory]
    [InlineData(OrderStatus.EnRoute)]
    [InlineData(OrderStatus.Delivered)]
    public async Task DispatchedOrders_BlockUnassignUnscheduleReassignmentAndDateChange(OrderStatus status)
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.AddOrderAsync(status);
        await using var db = fixture.CreateDb();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProductionAssignmentService(db).UnassignAsync(id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DeliveryService(db).UnscheduleDeliveryAsync(id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProductionAssignmentService(db).SetDeliveryDateAsync(id, new DateTime(2026, 10, 12)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DeliveryService(db).ScheduleDeliveryAsync(id, new DateTime(2026, 10, 12), null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnassignOrUnschedule_ReturnsApprovedPreservesDatesAndExcludesAutomaticDelivery(bool unassign)
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.AddOrderAsync();
        await using (var db = fixture.CreateDb())
        {
            if (unassign) await new ProductionAssignmentService(db).UnassignAsync(id);
            else await fixture.DeliveryService(db).UnscheduleDeliveryAsync(id);
        }
        fixture.Time.Now = fixture.Time.Now.AddDays(10);
        await using var verify = fixture.CreateDb();
        Assert.Equal(0, await fixture.Service(verify).CompleteDueOrdersAsync());
        var order = await verify.Orders.SingleAsync();
        Assert.Equal(OrderStatus.Approved, order.Status);
        Assert.False(order.IsAssignedToProduction);
        Assert.Equal(new DateTime(2026, 10, 10), order.DeliveryDate);
        Assert.Equal(order.DeliveryDate, order.OriginalCsvDeliveryDate);
        Assert.Empty(await verify.DeliverySchedules.ToListAsync());
    }

    [Fact]
    public async Task Scheduler_ConcurrentStatusChangeIsNotOverwritten()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.AddOrderAsync();
        fixture.Time.Now = fixture.Time.Now.AddDays(1);
        var interceptor = new BeforeSave(async () =>
        {
            await using var other = fixture.CreateDb();
            var changed = await other.Orders.SingleAsync(x => x.Id == id);
            changed.Status = OrderStatus.Approved;
            changed.IsAssignedToProduction = false;
            await other.SaveChangesAsync();
        });
        await using var db = fixture.CreateDb(interceptor);
        Assert.Equal(0, await fixture.Service(db).CompleteDueOrdersAsync());
        await using var verify = fixture.CreateDb();
        Assert.Equal(OrderStatus.Approved, (await verify.Orders.SingleAsync()).Status);
        Assert.Empty(await verify.AuditLogs.ToListAsync());
        Assert.Equal("Scheduled", (await verify.DeliverySchedules.SingleAsync()).Status);
    }

    [Fact]
    public async Task Reports_UsePersistedStatusesAndCountEachOrderOnce_IncludingSupplierAndExpectedTime()
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (var status in Enum.GetValues<OrderStatus>()) await fixture.AddOrderAsync(status);
        await using var db = fixture.CreateDb();
        var summary = await new ReportService(db, fixture.Clock).GetSummaryByDeliveryDateAsync(new DateTime(2026, 10, 10));
        Assert.Equal(9, summary.TotalOrders);
        Assert.Equal(summary.TotalOrders, summary.OrdersByStatus.Sum(x => x.Count));
        Assert.Equal(summary.TotalOrders, summary.DeliveryBreakdown.Sum(x => x.Count));
        Assert.Equal(9, summary.DeliverySummary.Select(x => x.Id).Distinct().Count());
        Assert.All(summary.OrdersByStatus, x => Assert.Equal(1, x.Count));
        Assert.All(summary.DeliverySummary, x => Assert.Equal("SAMS TISSUE PRODUCTS", x.Supplier));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task EnRoute_RejectsOrdersWithoutAssignmentOrSchedule(bool assigned, bool scheduled)
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.AddOrderAsync(assigned: assigned, scheduled: scheduled);
        await using var db = fixture.CreateDb();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service(db).SetEnRouteAsync(id, 1.5m));
        Assert.Null((await db.Orders.SingleAsync()).EnRouteAtUtc);
    }

    [Fact]
    public async Task DeliveryTransitions_PreservePlannerSequenceProductionRowsStockChainAndInvoiceExport()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.AddOrderAsync();
        await using var db = fixture.CreateDb();
        var production = new ProductionService(db, NullLogger<ProductionService>.Instance);
        var planner = new ProductionDeliveryPlannerService(db, production, fixture.Clock);
        var initial = await planner.GetCurrentPlanAsync();
        var orderRow = Assert.Single(initial.Events, x => x.OrderId == id);
        var withProduction = await planner.AddProductionEventAsync(orderRow.Id);
        var productionRow = Assert.Single(withProduction.Events, x => x.EventType == "Production");
        await planner.UpdateEventQuantitiesAsync(productionRow.Id, new ProductionDeliveryPlanQuantitiesUpdateDto
        {
            Quantities = new() { new ProductionDeliveryPlanProductQuantityDto { ProductId = initial.Products.Single().ProductId, Quantity = 10m } }
        });
        var before = await planner.GetCurrentPlanAsync();
        await fixture.Service(db).SetEnRouteAsync(id, 1.5m);
        fixture.Time.Now = fixture.Time.Now.AddHours(2);
        Assert.Equal(1, await fixture.Service(db).CompleteDueOrdersAsync());
        var after = await planner.GetCurrentPlanAsync();
        Assert.Equal(before.Events.Select(x => (x.Id, x.Sequence, x.EventType)), after.Events.Select(x => (x.Id, x.Sequence, x.EventType)));
        Assert.Equal(before.Events.SelectMany(x => x.StockAfter).Select(x => x.Quantity), after.Events.SelectMany(x => x.StockAfter).Select(x => x.Quantity));
        Assert.Equal("Delivered", Assert.Single(after.Events, x => x.OrderId == id).Status);
        Assert.False(Assert.Single(after.Events, x => x.OrderId == id).CanSetEnRoute);
        var exported = await new PastelExportService(db).GenerateInvoiceFileAsync(new DateTime(2026, 10, 10));
        Assert.Contains("TP18", System.Text.Encoding.UTF8.GetString(exported.Content));
    }

    [Fact]
    public void DeploymentConfiguration_UsesJohannesburgAndAValidPollingInterval()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OrderProcessingApp", "appsettings.json"));
        var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();
        var options = configuration.GetSection("DeliveryLifecycle").Get<DeliveryLifecycleOptions>()!;
        Assert.Equal("Africa/Johannesburg", options.BusinessTimeZone);
        Assert.InRange(options.PollIntervalSeconds, 1, 3600);
        var clock = new DeliveryClock(TimeProvider.System, Microsoft.Extensions.Options.Options.Create(options));
        Assert.Equal(TimeSpan.FromHours(2), clock.BusinessTimeZone.GetUtcOffset(new DateTime(2026, 10, 10)));
    }

    [Fact]
    public async Task HostedWorker_RunsWithoutDashboardAndCatchesUpFromPersistedDataOnRestart()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.AddOrderAsync();
        await using (var dispatchDb = fixture.CreateDb())
        {
            await fixture.Service(dispatchDb).SetEnRouteAsync(id, 1.5m);
        }
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<DeliveryLifecycleOptions>(options => options.PollIntervalSeconds = 1);
        services.AddSingleton<TimeProvider>(fixture.Time);
        services.AddSingleton<DeliveryClock>();
        services.AddScoped(_ => fixture.CreateDb());
        services.AddScoped<DeliveryLifecycleService>();
        services.AddHostedService<DeliveryLifecycleWorker>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var worker = Assert.Single(provider.GetServices<IHostedService>());
        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);
        fixture.Time.Now = fixture.Time.Now.AddHours(2);
        using var restarted = new DeliveryLifecycleWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<DeliveryClock>(),
            provider.GetRequiredService<IOptions<DeliveryLifecycleOptions>>(),
            NullLogger<DeliveryLifecycleWorker>.Instance);
        await restarted.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        bool delivered;
        do
        {
            await using var verify = fixture.CreateDb();
            delivered = await verify.Orders.AnyAsync(x => x.Status == OrderStatus.Delivered);
            if (!delivered) await Task.Delay(50);
        } while (!delivered && DateTime.UtcNow < deadline);
        await restarted.StopAsync(CancellationToken.None);
        Assert.True(delivered, "The real hosted worker must update the persisted order without a dashboard request.");
        Assert.Equal("Africa/Johannesburg", provider.GetRequiredService<DeliveryClock>().BusinessTimeZone.Id);
    }

    private sealed class FixedTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 10, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class BeforeSave(Func<Task> callback) : SaveChangesInterceptor
    {
        private bool _called;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_called)
            {
                _called = true;
                await callback();
            }
            return result;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        public FixedTime Time { get; } = new();
        public DeliveryClock Clock => new(Time, Microsoft.Extensions.Options.Options.Create(new DeliveryLifecycleOptions()));

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await fixture._connection.OpenAsync();
            await using var db = fixture.CreateDb();
            await db.Database.EnsureCreatedAsync();
            db.DistributionCentres.Add(new DistributionCentre { Name = "BASSON", Code = "BASSON", IsActive = true, Region = new Region { Name = "Gauteng" } });
            db.Products.Add(new Product { Name = "TOILET PAPER 2PLY HOUSEBRAND 18S PK", SKUCode = "TP18", IsActive = true });
            await db.SaveChangesAsync();
            return fixture;
        }

        public AppDbContext CreateDb(IInterceptor? interceptor = null)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            return new AppDbContext(options.Options);
        }

        public DeliveryLifecycleService Service(AppDbContext db) => new(db, Clock, NullLogger<DeliveryLifecycleService>.Instance);
        public DeliveryService DeliveryService(AppDbContext db) => new(db, new AuditService(db), NullLogger<DeliveryService>.Instance);

        public async Task<int> AddOrderAsync(OrderStatus status = OrderStatus.Scheduled,
            bool assigned = true, bool scheduled = true, DateTime? date = null)
        {
            await using var db = CreateDb();
            var order = new Order
            {
                OrderNumber = $"PO-{await db.Orders.CountAsync() + 1}",
                OrderDate = new DateTime(2026, 10, 1),
                DeliveryDate = date ?? new DateTime(2026, 10, 10),
                OriginalCsvDeliveryDate = new DateTime(2026, 10, 10),
                Source = OrderSource.CSV,
                DistributionCentreId = (await db.DistributionCentres.SingleAsync()).Id,
                Status = status,
                IsAssignedToProduction = assigned,
                Items = new List<OrderItem>
                {
                    new() { ProductId = (await db.Products.SingleAsync()).Id, Quantity = 5m, Price = 2m,
                        Metadata = new Dictionary<string, string> { ["Vendor"] = "SAMS TISSUE PRODUCTS" } }
                }
            };
            if (scheduled) order.DeliverySchedules.Add(new DeliverySchedule { DeliveryDate = order.DeliveryDate.Value, Status = "Scheduled" });
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            return order.Id;
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}
