using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderProcessingApp.Data;
using OrderProcessingApp.Models;
using OrderProcessingApp.Services;
using Xunit;

namespace OrderProcessingApp.Tests;

public class PriceListBulkSaveTests
{
    [Fact]
    public async Task ApplyPriceToDistributionCentresAsync_OneProductOneDcStillWorks()
    {
        await using var fixture = await TestFixture.CreateAsync();

        var product = await fixture.CreateProductAsync("Product 1", "SKU-1");
        var dc = await fixture.CreateDistributionCentreAsync("DC North");

        var service = fixture.CreateAdminService();
        var saved = await service.ApplyPriceToDistributionCentresAsync(product.Id, new[] { dc.Id }, 12.50m);

        Assert.Equal(1, saved.Count);
        Assert.Equal(1, saved.CreatedCount);
        Assert.Single(saved.CreatedIds);
        Assert.Contains(dc.Id, saved.CreatedIds);

        await using var db = new AppDbContext(fixture.Options);
        var persisted = await db.PriceLists.Where(x => x.ProductId == product.Id && x.DistributionCentreId == dc.Id).ToListAsync();
        var persistedRow = Assert.Single(persisted);
        Assert.Equal(12.50m, persistedRow.Price);
        Assert.True(persistedRow.IsActive);
    }

    [Fact]
    public async Task ApplyPriceToDistributionCentresAsync_ReturnsCreatedRestoredAndUpdatedCounts()
    {
        await using var fixture = await TestFixture.CreateAsync();

        var product = await fixture.CreateProductAsync("Product Mixed", "SKU-MIXED");
        var dcCreate = await fixture.CreateDistributionCentreAsync("DC Create");
        var dcRestore = await fixture.CreateDistributionCentreAsync("DC Restore");
        var dcUpdate = await fixture.CreateDistributionCentreAsync("DC Update");

        await fixture.CreateInactivePriceListAsync(product.Id, dcRestore.Id, 5.00m);
        await fixture.CreatePriceListAsync(product.Id, dcUpdate.Id, 8.00m);

        var service = fixture.CreateAdminService();
        var saved = await service.ApplyPriceToDistributionCentresAsync(product.Id, new[] { dcCreate.Id, dcRestore.Id, dcUpdate.Id }, 11.00m);

        Assert.Equal(3, saved.Count);
        Assert.Equal(1, saved.CreatedCount);
        Assert.Equal(1, saved.RestoredCount);
        Assert.Equal(1, saved.UpdatedCount);

        await using var db = new AppDbContext(fixture.Options);
        var createdRow = await db.PriceLists.IgnoreQueryFilters().SingleAsync(x => x.ProductId == product.Id && x.DistributionCentreId == dcCreate.Id);
        var restoredRow = await db.PriceLists.IgnoreQueryFilters().SingleAsync(x => x.ProductId == product.Id && x.DistributionCentreId == dcRestore.Id);
        var updatedRow = await db.PriceLists.IgnoreQueryFilters().SingleAsync(x => x.ProductId == product.Id && x.DistributionCentreId == dcUpdate.Id);

        Assert.Contains(createdRow.Id, saved.CreatedIds);
        Assert.Contains(restoredRow.Id, saved.RestoredIds);
        Assert.Contains(updatedRow.Id, saved.UpdatedIds);
    }

    [Fact]
    public async Task ApplyPriceToDistributionCentresAsync_CreatesSamePriceForAllSelectedDcs()
    {
        await using var fixture = await TestFixture.CreateAsync();

        var product = await fixture.CreateProductAsync("Product Multi", "SKU-MULTI");
        var dcNorth = await fixture.CreateDistributionCentreAsync("DC North");
        var dcSouth = await fixture.CreateDistributionCentreAsync("DC South");
        var dcCanelands = await fixture.CreateDistributionCentreAsync("DC Canelands");

        var service = fixture.CreateAdminService();
        var saved = await service.ApplyPriceToDistributionCentresAsync(product.Id, new[] { dcNorth.Id, dcSouth.Id, dcCanelands.Id }, 12.00m);

        Assert.Equal(3, saved.Count);
        Assert.Equal(3, saved.CreatedCount);
        Assert.All(saved.CreatedIds, id => Assert.Contains(id, new[] { dcNorth.Id, dcSouth.Id, dcCanelands.Id }));

        await using var db = new AppDbContext(fixture.Options);
        var rows = await db.PriceLists
            .Where(x => x.ProductId == product.Id)
            .OrderBy(x => x.DistributionCentreId)
            .ToListAsync();

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal(12.00m, row.Price));
    }

    [Fact]
    public async Task ApplyPriceToDistributionCentresAsync_UpdatesSelectedExistingDc_LeavesUnselectedUnchanged()
    {
        await using var fixture = await TestFixture.CreateAsync();

        var product = await fixture.CreateProductAsync("Product Update", "SKU-UPD");
        var dcNorth = await fixture.CreateDistributionCentreAsync("DC North");
        var dcSouth = await fixture.CreateDistributionCentreAsync("DC South");
        var dcCanelands = await fixture.CreateDistributionCentreAsync("DC Canelands");

        await fixture.CreatePriceListAsync(product.Id, dcNorth.Id, 15.00m);
        await fixture.CreatePriceListAsync(product.Id, dcSouth.Id, 20.00m);

        var service = fixture.CreateAdminService();
        var saved = await service.ApplyPriceToDistributionCentresAsync(product.Id, new[] { dcNorth.Id, dcCanelands.Id }, 12.00m);

        Assert.Equal(2, saved.Count);
        Assert.Equal(1, saved.UpdatedCount);
        Assert.Equal(1, saved.CreatedCount);
        Assert.Contains(dcNorth.Id, saved.UpdatedIds);
        Assert.Contains(dcCanelands.Id, saved.CreatedIds);

        await using var db = new AppDbContext(fixture.Options);
        var north = await db.PriceLists.FirstAsync(x => x.ProductId == product.Id && x.DistributionCentreId == dcNorth.Id);
        var south = await db.PriceLists.FirstAsync(x => x.ProductId == product.Id && x.DistributionCentreId == dcSouth.Id);
        var canelands = await db.PriceLists.FirstAsync(x => x.ProductId == product.Id && x.DistributionCentreId == dcCanelands.Id);

        Assert.Equal(12.00m, north.Price);
        Assert.Equal(20.00m, south.Price);
        Assert.Equal(12.00m, canelands.Price);
    }

    [Fact]
    public async Task ApplyPriceToDistributionCentresAsync_DoesNotCreateDuplicateRecordsForSameProductDc()
    {
        await using var fixture = await TestFixture.CreateAsync();

        var product = await fixture.CreateProductAsync("Product Dedupe", "SKU-DEDUPE");
        var dc = await fixture.CreateDistributionCentreAsync("DC North");

        await fixture.CreatePriceListAsync(product.Id, dc.Id, 10.00m);

        var service = fixture.CreateAdminService();
        var saved = await service.ApplyPriceToDistributionCentresAsync(product.Id, new[] { dc.Id, dc.Id }, 11.50m);

        Assert.Equal(1, saved.Count);
        Assert.Equal(1, saved.UpdatedCount);

        await using var db = new AppDbContext(fixture.Options);
        var rows = await db.PriceLists.Where(x => x.ProductId == product.Id && x.DistributionCentreId == dc.Id).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(11.50m, rows[0].Price);
    }

    [Fact]
    public async Task ApplyPriceToDistributionCentresAsync_RestoresInactiveSelectedRow()
    {
        await using var fixture = await TestFixture.CreateAsync();

        var product = await fixture.CreateProductAsync("Product Restore", "SKU-RESTORE");
        var dc = await fixture.CreateDistributionCentreAsync("DC Restore");

        await fixture.CreateInactivePriceListAsync(product.Id, dc.Id, 8.00m);

        var service = fixture.CreateAdminService();
        var saved = await service.ApplyPriceToDistributionCentresAsync(product.Id, new[] { dc.Id }, 9.00m);

        Assert.Equal(1, saved.Count);
        Assert.Equal(1, saved.RestoredCount);
        Assert.Contains(dc.Id, saved.RestoredIds);

        await using var db = new AppDbContext(fixture.Options);
        var persisted = await db.PriceLists.IgnoreQueryFilters().FirstAsync(x => x.ProductId == product.Id && x.DistributionCentreId == dc.Id);
        Assert.Equal(9.00m, persisted.Price);
        Assert.True(persisted.IsActive);
    }

    [Fact]
    public async Task ApplyPriceToDistributionCentresAsync_DifferentProductsRemainIsolated()
    {
        await using var fixture = await TestFixture.CreateAsync();

        var productA = await fixture.CreateProductAsync("Product A", "SKU-A");
        var productB = await fixture.CreateProductAsync("Product B", "SKU-B");
        var dcNorth = await fixture.CreateDistributionCentreAsync("DC North");
        var dcSouth = await fixture.CreateDistributionCentreAsync("DC South");

        await fixture.CreatePriceListAsync(productA.Id, dcNorth.Id, 5.00m);
        await fixture.CreatePriceListAsync(productB.Id, dcNorth.Id, 8.00m);

        var service = fixture.CreateAdminService();
        var result = await service.ApplyPriceToDistributionCentresAsync(productA.Id, new[] { dcNorth.Id, dcSouth.Id }, 7.00m);

        Assert.Equal(2, result.Count);

        await using var db = new AppDbContext(fixture.Options);
        var productARow = await db.PriceLists.FirstAsync(x => x.ProductId == productA.Id && x.DistributionCentreId == dcNorth.Id);
        var productBRow = await db.PriceLists.FirstAsync(x => x.ProductId == productB.Id && x.DistributionCentreId == dcNorth.Id);

        Assert.Equal(7.00m, productARow.Price);
        Assert.Equal(8.00m, productBRow.Price);
    }

    [Fact]
    public async Task PricingService_ResolvesCorrectPriceByProductAndDistributionCentreAfterBulkSave()
    {
        await using var fixture = await TestFixture.CreateAsync();

        var product = await fixture.CreateProductAsync("Product Lookup", "SKU-LOOKUP");
        var dcNorth = await fixture.CreateDistributionCentreAsync("DC North");
        var dcSouth = await fixture.CreateDistributionCentreAsync("DC South");

        var service = fixture.CreateAdminService();
        await service.ApplyPriceToDistributionCentresAsync(product.Id, new[] { dcNorth.Id, dcSouth.Id }, 13.00m);

        await using var db = new AppDbContext(fixture.Options);
        var pricingService = new PricingService(db, NullLogger<PricingService>.Instance);

        var north = await pricingService.GetEffectivePriceAsync(product.Id, dcNorth.Id);
        var south = await pricingService.GetEffectivePriceAsync(product.Id, dcSouth.Id);

        Assert.True(north.IsFound);
        Assert.True(south.IsFound);
        Assert.Equal(13.00m, north.EffectivePrice);
        Assert.Equal(13.00m, south.EffectivePrice);
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
            var dbName = $"pricelist-bulk-save-tests-{Guid.NewGuid():N}";
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

        public async Task CreateInactivePriceListAsync(int productId, int distributionCentreId, decimal price)
        {
            await using var db = new AppDbContext(Options);
            var entry = new PriceList
            {
                ProductId = productId,
                DistributionCentreId = distributionCentreId,
                Price = price,
                IsActive = false
            };

            db.PriceLists.Add(entry);
            await db.SaveChangesAsync();
        }

        public AdminService CreateAdminService()
        {
            return new AdminService(new AppDbContext(Options));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
