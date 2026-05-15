using OrderProcessingApp.DTOs;

namespace OrderProcessingApp.Services;

public interface IProductionService
{
    Task<ProductionResponseDto> GetProductionAsync(DateTime? date, CancellationToken cancellationToken = default);
    Task<ProductionResponseDto> GetProductionByDateAsync(DateTime date, CancellationToken cancellationToken = default);
    Task<List<ProductionPlanDto>> CreateAsync(List<int> orderIds, CancellationToken cancellationToken = default);
    Task<ProductionDecisionResultDto> SaveProductionDecisionsAsync(SaveProductionDecisionsDto dto, CancellationToken cancellationToken = default);
    Task CreateOrUpdatePlanAsync(ProductionPlanUpsertDto dto, CancellationToken cancellationToken = default);
    Task<List<ProductionPlanDto>> GetPlansByDateAsync(DateTime date, CancellationToken cancellationToken = default);
    Task<List<StockCheckDto>> CheckStockAsync(DateTime date, CancellationToken cancellationToken = default);
}
