using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;

namespace OrderProcessingApp.Services;

public class PlanningService : IPlanningService
{
    private readonly AppDbContext _dbContext;

    public PlanningService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<PlanningCheckResult> CheckStockVsProductionRequirementsAsync(int productId, decimal requiredQuantity, DateTime deliveryDate, CancellationToken cancellationToken = default)
    {
        var planDate = DateTime.SpecifyKind(deliveryDate.Date, DateTimeKind.Unspecified);

        var latestPlan = await _dbContext.ProductionPlans
            .AsNoTracking()
            .Where(x => x.ProductId == productId && x.Date <= planDate)
            .OrderByDescending(x => x.Date)
            .FirstOrDefaultAsync(cancellationToken);

        var available = latestPlan is null ? 0 : latestPlan.OpeningStock + latestPlan.ProductionQuantity;
        var shortfall = requiredQuantity > available ? requiredQuantity - available : 0;

        return new PlanningCheckResult
        {
            IsSufficient = shortfall <= 0,
            AvailableQuantity = available,
            RequiredQuantity = requiredQuantity,
            Shortfall = shortfall
        };
    }
}
