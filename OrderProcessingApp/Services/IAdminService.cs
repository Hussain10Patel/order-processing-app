namespace OrderProcessingApp.Services;

public interface IAdminService
{
    Task ResetDataAsync(CancellationToken cancellationToken = default);
}