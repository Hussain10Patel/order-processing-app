namespace OrderProcessingApp.Services;

public interface IPalletService
{
    Task<decimal> CalculatePalletsAsync(int productId, decimal quantity, CancellationToken cancellationToken = default);
}
