namespace OrderProcessingApp.Services;

public interface IProductService
{
    Task<bool> SoftDeleteProductAsync(int id, CancellationToken cancellationToken = default);
}
