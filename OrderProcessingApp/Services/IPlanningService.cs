namespace OrderProcessingApp.Services;

public class PlanningCheckResult
{
    public bool IsSufficient { get; set; }
    public decimal AvailableQuantity { get; set; }
    public decimal RequiredQuantity { get; set; }
    public decimal Shortfall { get; set; }
}

public interface IPlanningService
{
    Task<PlanningCheckResult> CheckStockVsProductionRequirementsAsync(int productId, decimal requiredQuantity, DateTime deliveryDate, CancellationToken cancellationToken = default);
}
