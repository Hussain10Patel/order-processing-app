using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;

namespace OrderProcessingApp.Services;

public class AdminService : IAdminService
{
    private readonly AppDbContext _dbContext;

    public AdminService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task ResetDataAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        await _dbContext.Database.ExecuteSqlRawAsync(@"
            TRUNCATE TABLE
                ""AuditLogs"",
                ""DeliverySchedules"",
                ""OrderItems"",
                ""Orders"",
                ""ProductionPlans""
            RESTART IDENTITY CASCADE;
        ", cancellationToken);

        await SeedDataExtensions.SeedDemoWorkflowDataForDevelopmentAsync(_dbContext, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<(Product Product, bool Restored)> CreateOrRestoreProductAsync(string name, string skuCode, decimal palletConversionRate, CancellationToken cancellationToken = default)
    {
        var normalizedSku = NormalizeSku(skuCode);
        var existing = await _dbContext.Products
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.SKUCode != null && x.SKUCode.Trim().ToLower() == normalizedSku, cancellationToken);

        if (existing is not null)
        {
            if (existing.IsActive)
            {
                throw new InvalidOperationException("This item already exists");
            }

            existing.IsActive = true;
            existing.Name = name;
            existing.SKUCode = normalizedSku;
            existing.PalletConversionRate = palletConversionRate;
            existing.IsMapped = true;
            existing.RequiresAttention = false;

            await _dbContext.SaveChangesAsync(cancellationToken);
            return (existing, true);
        }

        var entity = new Product
        {
            Name = name,
            SKUCode = normalizedSku,
            PalletConversionRate = palletConversionRate,
            IsMapped = true,
            RequiresAttention = false,
            IsActive = true
        };

        _dbContext.Products.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (entity, false);
    }

    public async Task<(PriceList PriceList, bool Restored)> CreateOrRestorePriceListAsync(int productId, int distributionCentreId, decimal price, CancellationToken cancellationToken = default)
    {
        var productExists = await _dbContext.Products
            .AsNoTracking()
            .AnyAsync(x => x.Id == productId, cancellationToken);
        if (!productExists)
        {
            throw new KeyNotFoundException("Product not found.");
        }

        var distributionCentreExists = await _dbContext.DistributionCentres
            .AsNoTracking()
            .AnyAsync(x => x.Id == distributionCentreId, cancellationToken);
        if (!distributionCentreExists)
        {
            throw new KeyNotFoundException("Invalid distribution centre");
        }

        var existing = await _dbContext.PriceLists
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.ProductId == productId && x.DistributionCentreId == distributionCentreId, cancellationToken);

        if (existing is not null)
        {
            if (existing.IsActive)
            {
                throw new InvalidOperationException("This item already exists");
            }

            existing.IsActive = true;
            existing.Price = price;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return (existing, true);
        }

        var entity = new PriceList
        {
            ProductId = productId,
            DistributionCentreId = distributionCentreId,
            Price = price,
            IsActive = true
        };

        _dbContext.PriceLists.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (entity, false);
    }

    public async Task<PriceListBulkApplyResult> ApplyPriceToDistributionCentresAsync(int productId, IReadOnlyCollection<int> distributionCentreIds, decimal price, CancellationToken cancellationToken = default)
    {
        if (distributionCentreIds is null)
        {
            throw new ArgumentNullException(nameof(distributionCentreIds));
        }

        var uniqueDistributionCentreIds = distributionCentreIds
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        if (uniqueDistributionCentreIds.Count == 0)
        {
            throw new InvalidOperationException("Please select at least one distribution centre.");
        }

        var productExists = await _dbContext.Products
            .AsNoTracking()
            .AnyAsync(x => x.Id == productId, cancellationToken);
        if (!productExists)
        {
            throw new KeyNotFoundException("Product not found.");
        }

        var validDistributionCentreIds = await _dbContext.DistributionCentres
            .AsNoTracking()
            .Where(x => uniqueDistributionCentreIds.Contains(x.Id))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        var invalidIds = uniqueDistributionCentreIds.Except(validDistributionCentreIds).ToList();
        if (invalidIds.Count > 0)
        {
            throw new KeyNotFoundException($"Invalid distribution centre: {invalidIds[0]}");
        }

        var rows = new List<PriceList>();
        var createdEntities = new List<PriceList>();
        var restoredIds = new List<int>();
        var updatedIds = new List<int>();
        IDbContextTransaction? transaction = null;

        try
        {
            if (_dbContext.Database.IsRelational())
            {
                transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            }

            foreach (var distributionCentreId in uniqueDistributionCentreIds)
            {
                var existing = await _dbContext.PriceLists
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.ProductId == productId && x.DistributionCentreId == distributionCentreId, cancellationToken);

                if (existing is not null)
                {
                    if (existing.IsActive)
                    {
                        existing.Price = price;
                        rows.Add(existing);
                        updatedIds.Add(existing.Id);
                        continue;
                    }

                    existing.IsActive = true;
                    existing.Price = price;
                    rows.Add(existing);
                    restoredIds.Add(existing.Id);
                    continue;
                }

                var entity = new PriceList
                {
                    ProductId = productId,
                    DistributionCentreId = distributionCentreId,
                    Price = price,
                    IsActive = true
                };

                _dbContext.PriceLists.Add(entity);
                rows.Add(entity);
                createdEntities.Add(entity);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            var createdIds = createdEntities
                .Where(x => x.Id > 0)
                .Select(x => x.Id)
                .ToList();

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return new PriceListBulkApplyResult
            {
                Count = rows.Count,
                CreatedCount = createdIds.Count,
                RestoredCount = restoredIds.Count,
                UpdatedCount = updatedIds.Count,
                CreatedIds = createdIds,
                RestoredIds = restoredIds,
                UpdatedIds = updatedIds
            };
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
    }

    public async Task<(DistributionCentre DistributionCentre, bool Restored)> CreateOrRestoreDistributionCentreAsync(string name, int? sourceDistributionCentreId, CancellationToken cancellationToken = default)
    {
        var existing = await _dbContext.DistributionCentres
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Name == name || x.Code == name, cancellationToken);

        if (!sourceDistributionCentreId.HasValue || sourceDistributionCentreId.Value <= 0)
        {
            sourceDistributionCentreId = await _dbContext.DistributionCentres
                .AsNoTracking()
                .OrderBy(x => x.Id)
                .Select(x => (int?)x.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (!sourceDistributionCentreId.HasValue)
        {
            throw new InvalidOperationException("DistributionCentreId is required.");
        }

        var sourceDistributionCentre = await _dbContext.DistributionCentres
            .AsNoTracking()
            .Where(x => x.Id == sourceDistributionCentreId.Value)
            .Select(x => new { x.RegionId })
            .FirstOrDefaultAsync(cancellationToken);

        if (sourceDistributionCentre is null)
        {
            throw new InvalidOperationException("Invalid distribution centre.");
        }

        if (existing is not null)
        {
            if (existing.IsActive)
            {
                throw new InvalidOperationException("This item already exists");
            }

            existing.IsActive = true;
            existing.Name = name;
            existing.Code = name;
            existing.RegionId = sourceDistributionCentre.RegionId;
            existing.RequiresAttention = false;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return (existing, true);
        }

        var entity = new DistributionCentre
        {
            Name = name,
            Code = name,
            RegionId = sourceDistributionCentre.RegionId,
            RequiresAttention = false,
            IsActive = true
        };

        _dbContext.DistributionCentres.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (entity, false);
    }

    public async Task<bool> SoftDeleteProductAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.Products
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (entity is null)
        {
            return false;
        }

        entity.IsActive = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SoftDeletePriceListAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.PriceLists
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (entity is null)
        {
            return false;
        }

        var inUse = await _dbContext.Orders
            .AnyAsync(o => o.DistributionCentreId == entity.DistributionCentreId
                && o.Items.Any(i => i.ProductId == entity.ProductId), cancellationToken);

        if (inUse)
        {
            throw new InvalidOperationException("Cannot delete, entity is in use");
        }

        entity.IsActive = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SoftDeleteDistributionCentreAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.DistributionCentres
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (entity is null)
        {
            return false;
        }

        var inUse = await _dbContext.Orders
            .AnyAsync(o => o.DistributionCentreId == id, cancellationToken);

        if (inUse)
        {
            throw new InvalidOperationException("Cannot delete, entity is in use");
        }

        entity.IsActive = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string NormalizeSku(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().ToLowerInvariant();
    }
}