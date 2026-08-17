using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using OrderProcessingApp.Controllers;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;
using OrderProcessingApp.Services;
using Xunit;

namespace OrderProcessingApp.Tests;

public class CsvImportMappingTests
{
    [Fact]
    public async Task UploadCsv_UsesQtyForQuantity_AndGrossCstForImportedPrice_AcrossDownstreamConsumers()
    {
        await using var fixture = await CsvImportFixture.CreateAsync();

        const string csv =
            "OrderNo|Vendor|Depot|OrderDate|DropDate|OrderCode|Buyer|Dept|SubDept|DestID|DestDesc|DestEAN|ItemNum|ItemDesc|Barcode|SuppItemNo|WHOrderInd|ItemPackSize|Qty|ContractNo|Costper|CostUnitMeasure|GrossCst|ExetendCst|Freestock\n" +
            "1204466650|SAMS TISSUE PRODUCTS (PTY) LTD|SAMS TISSUE PRODUCTS (PTY) LTD|2026/03/27|2026/04/02|N|594182|||G962|DC NELLWYN JOHANNESBURG|6001000000000|6001001361002|TOILET PAPER 2PLY WHITE HOUSEBRAND 9S|6001001361002|| |8|448||8.00|EA|424.28||0\n" +
            "1204466651|SAMS TISSUE PRODUCTS (PTY) LTD|SAMS TISSUE PRODUCTS (PTY) LTD|2026/03/27|2026/04/02|N|594182|||G962|DC NELLWYN JOHANNESBURG|6001000000001|6001001361003|TOILET PAPER 1PLY HOUSEBRAND 4S PK|6001001361003|| |6|120||12.00|EA|199.55||0\n";

        var result = await fixture.UploadCsvAsync(csv);
        Assert.Equal(2, result.CreatedOrders);
        Assert.Empty(result.ValidationErrors);

        await using var db = fixture.CreateDbContext();
        var orders = await db.Orders
            .Include(x => x.Items)
            .OrderBy(x => x.OrderNumber)
            .ToListAsync();

        Assert.Equal(2, orders.Count);

        var firstOrder = Assert.Single(orders, x => x.OrderNumber == "1204466650");
        var firstItem = Assert.Single(firstOrder.Items);
        Assert.Equal(448m, firstItem.Quantity);
        Assert.Equal(424.28m, firstItem.Price);
        Assert.Equal("8", firstItem.Metadata["ItemPackSize"]);
        Assert.Equal("448", firstItem.Metadata["QtyRaw"]);
        Assert.Equal("424.28", firstItem.Metadata["ImportedPriceRaw"]);
        Assert.Equal("GrossCst", firstItem.Metadata["ImportedPriceSource"]);
        Assert.Equal("B2BOrders", firstItem.Metadata["DetectedCsvSchema"]);
        Assert.Equal(448m * 424.28m, firstOrder.TotalValue);

        var secondOrder = Assert.Single(orders, x => x.OrderNumber == "1204466651");
        var secondItem = Assert.Single(secondOrder.Items);
        Assert.Equal(120m, secondItem.Quantity);
        Assert.Equal(199.55m, secondItem.Price);
        Assert.Equal("6", secondItem.Metadata["ItemPackSize"]);
        Assert.Equal("199.55", secondItem.Metadata["ImportedPriceRaw"]);
        Assert.Equal("GrossCst", secondItem.Metadata["ImportedPriceSource"]);
        Assert.Equal("B2BOrders", secondItem.Metadata["DetectedCsvSchema"]);
        Assert.Equal(120m * 199.55m, secondOrder.TotalValue);

        var firstOrderDto = await fixture.OrderService.GetOrderByIdAsync(firstOrder.Id);
        Assert.NotNull(firstOrderDto);
        var firstOrderDtoItem = Assert.Single(firstOrderDto!.Items);
        Assert.Equal(448m, firstOrderDtoItem.Quantity);
        Assert.Equal(424.28m, firstOrderDtoItem.Price);
        Assert.Equal(448m * 424.28m, firstOrderDtoItem.LineTotal);

        firstOrder.Status = OrderStatus.Approved;
        secondOrder.Status = OrderStatus.Approved;
        await db.SaveChangesAsync();

        var production = await fixture.CreateProductionService().GetProductionAsync(null);
        var productionOrder = Assert.Single(production.Orders, x => x.OrderNumber == "1204466650");
        var productionItem = Assert.Single(productionOrder.Items);
        Assert.Equal(448m, productionItem.Quantity);

        var report = await fixture.CreateReportService().GetSummaryByDeliveryDateAsync(new DateTime(2026, 4, 2));
        Assert.Contains(report.DeliverySummary, x => x.PoNumber == "1204466650");
        Assert.Contains(report.DeliverySummary, x => x.PoNumber == "1204466651");
        Assert.Equal((448m * 424.28m) + (120m * 199.55m), report.TotalValue);

        var ordersExport = await fixture.CreateExportService().ExportOrdersToExcelAsync(new DateTime(2026, 4, 2));
        var ordersCsv = DecodeCsv(ordersExport.Content);
        Assert.Contains("1204466650", ordersCsv);
        Assert.Contains("|448|424,28|", ordersCsv);
        Assert.Contains("1204466651", ordersCsv);
        Assert.Contains("|120|199,55|", ordersCsv);
    }

    [Fact]
    public async Task UploadCsv_B2BSchema_RequiresGrossCstForPrice()
    {
        await using var fixture = await CsvImportFixture.CreateAsync();

        const string csv =
            "OrderNo|Vendor|Depot|OrderDate|DropDate|OrderCode|Buyer|Dept|SubDept|DestID|DestDesc|DestEAN|ItemNum|ItemDesc|Barcode|SuppItemNo|WHOrderInd|ItemPackSize|Qty|ContractNo|Costper|CostUnitMeasure|GrossCst|ExetendCst|Freestock\n" +
            "1204466652|SAMS TISSUE PRODUCTS (PTY) LTD|SAMS TISSUE PRODUCTS (PTY) LTD|2026/03/27|2026/04/02|N|594182|||G962|DC NELLWYN JOHANNESBURG|6001000000002|6001001361004|TOILET PAPER 2PLY HOUSEBRAND 4S PK|6001001361004|| |4|300||9.00|EA||318.45|0\n";

        var result = await fixture.UploadCsvAsync(csv);
        Assert.Equal(0, result.CreatedOrders);
        Assert.Contains(result.ValidationErrors, error =>
            string.Equals(error.Field, "Price", StringComparison.OrdinalIgnoreCase)
            && error.Message.Contains("requires GrossCst", StringComparison.OrdinalIgnoreCase));

        await using var db = fixture.CreateDbContext();
        Assert.Empty(await db.Orders.ToListAsync());
    }

    [Fact]
    public async Task UploadCsv_UnknownSchema_DoesNotAutoUseGrossCstAsPrice()
    {
        await using var fixture = await CsvImportFixture.CreateAsync();

        const string csv =
            "OrderNo,OrderDate,DistributionCentre,ProductCode,Qty,GrossCst\n" +
            "200001,2026-03-27,DC NELLWYN JOHANNESBURG,SKU-001,25,999.99\n";

        var result = await fixture.UploadCsvAsync(csv);
        Assert.Equal(0, result.CreatedOrders);
        Assert.Contains(result.ValidationErrors, error =>
            string.Equals(error.Field, "Price", StringComparison.OrdinalIgnoreCase)
            && error.Message.Contains("cannot be determined safely", StringComparison.OrdinalIgnoreCase));

        await using var db = fixture.CreateDbContext();
        Assert.Empty(await db.Orders.ToListAsync());
    }

    [Fact]
    public async Task UploadCsv_B2B_ItemPackSizeNeverBecomesQuantity()
    {
        await using var fixture = await CsvImportFixture.CreateAsync();

        const string csv =
            "OrderNo|Vendor|Depot|OrderDate|DropDate|OrderCode|Buyer|Dept|SubDept|DestID|DestDesc|DestEAN|ItemNum|ItemDesc|Barcode|SuppItemNo|WHOrderInd|ItemPackSize|Qty|ContractNo|Costper|CostUnitMeasure|GrossCst|ExetendCst|Freestock\n" +
            "1204467777|SAMS TISSUE PRODUCTS (PTY) LTD|SAMS TISSUE PRODUCTS (PTY) LTD|2026/03/27|2026/04/02|N|594182|||G962|DC NELLWYN JOHANNESBURG|6001000000002|6001001361004|TOILET PAPER 2PLY HOUSEBRAND 4S PK|6001001361004|| |2|17||9.00|EA|3.40||0\n";

        var result = await fixture.UploadCsvAsync(csv);
        Assert.Equal(1, result.CreatedOrders);
        Assert.Empty(result.ValidationErrors);

        await using var db = fixture.CreateDbContext();
        var order = await db.Orders.Include(x => x.Items).SingleAsync(x => x.OrderNumber == "1204467777");
        var item = Assert.Single(order.Items);

        Assert.Equal("2", item.Metadata["ItemPackSize"]);
        Assert.Equal("17", item.Metadata["QtyRaw"]);
        Assert.Equal(17m, item.Quantity);
        Assert.NotEqual(2m, item.Quantity);
        Assert.Equal(3.40m, item.Price);
    }

    private static string DecodeCsv(byte[] content)
    {
        var text = Encoding.UTF8.GetString(content);
        return text.TrimStart('\uFEFF');
    }

    private sealed class CsvImportFixture : IAsyncDisposable
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public IOrderService OrderService { get; }

        private CsvImportFixture(DbContextOptions<AppDbContext> options, IOrderService orderService)
        {
            _options = options;
            OrderService = orderService;
        }

        public static async Task<CsvImportFixture> CreateAsync()
        {
            var dbName = $"csv-import-tests-{Guid.NewGuid():N}";
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            await using var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var region = new Region { Name = "Region 1" };
            db.Regions.Add(region);
            await db.SaveChangesAsync();

            db.DistributionCentres.Add(new DistributionCentre
            {
                Name = "DC NELLWYN JOHANNESBURG",
                Code = "G962",
                RegionId = region.Id,
                IsActive = true
            });

            await db.SaveChangesAsync();

            var orderService = CreateOrderService(options);
            return new CsvImportFixture(options, orderService);
        }

        public AppDbContext CreateDbContext() => new(_options);

        public ProductionService CreateProductionService()
        {
            var db = new AppDbContext(_options);
            return new ProductionService(db, NullLogger<ProductionService>.Instance);
        }

        public ReportService CreateReportService()
        {
            var db = new AppDbContext(_options);
            return new ReportService(db);
        }

        public ExportService CreateExportService()
        {
            var db = new AppDbContext(_options);
            return new ExportService(db);
        }

        public async Task<CsvUploadResultDto> UploadCsvAsync(string csvText)
        {
            var controller = new UploadController(
                OrderService,
                new PendingCsvImportService(new AppDbContext(_options)),
                NullLogger<UploadController>.Instance);

            var file = BuildFormFile(csvText, "orders.csv");
            var action = await controller.UploadCsv(new List<IFormFile> { file }, createMissingProducts: true);
            var ok = Assert.IsType<OkObjectResult>(action.Result);
            return Assert.IsType<CsvUploadResultDto>(ok.Value);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static IOrderService CreateOrderService(DbContextOptions<AppDbContext> options)
        {
            var db = new AppDbContext(options);
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

        private static IFormFile BuildFormFile(string content, string fileName)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var stream = new MemoryStream(bytes);
            return new FormFile(stream, 0, bytes.Length, "files", fileName)
            {
                Headers = new HeaderDictionary(),
                ContentType = "text/csv"
            };
        }
    }
}