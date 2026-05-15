namespace OrderProcessingApp.Services;

using OrderProcessingApp.Models;

public interface IAdminService
{
    Task ResetDataAsync(CancellationToken cancellationToken = default);
    Task<(Product Product, bool Restored)> CreateOrRestoreProductAsync(string name, string skuCode, decimal palletConversionRate, CancellationToken cancellationToken = default);
    Task<(PriceList PriceList, bool Restored)> CreateOrRestorePriceListAsync(int productId, int distributionCentreId, decimal price, CancellationToken cancellationToken = default);
    Task<(DistributionCentre DistributionCentre, bool Restored)> CreateOrRestoreDistributionCentreAsync(string name, int? sourceDistributionCentreId, CancellationToken cancellationToken = default);
    Task<bool> SoftDeleteProductAsync(int id, CancellationToken cancellationToken = default);
    Task<bool> SoftDeletePriceListAsync(int id, CancellationToken cancellationToken = default);
    Task<bool> SoftDeleteDistributionCentreAsync(int id, CancellationToken cancellationToken = default);
}