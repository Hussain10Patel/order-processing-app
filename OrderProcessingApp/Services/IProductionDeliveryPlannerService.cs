using OrderProcessingApp.DTOs;

namespace OrderProcessingApp.Services;

public interface IProductionDeliveryPlannerService
{
    Task<ProductionDeliveryPlanDto> GetCurrentPlanAsync(CancellationToken cancellationToken = default);
    Task<ProductionDeliveryPlanDto> UpdateOpeningStockAsync(ProductionDeliveryPlanQuantitiesUpdateDto dto, CancellationToken cancellationToken = default);
    Task<ProductionDeliveryPlanDto> AddProductionEventAsync(int afterEventId, CancellationToken cancellationToken = default);
    Task<ProductionDeliveryPlanDto> AddStockAdjustmentEventAsync(int afterEventId, CancellationToken cancellationToken = default);
    Task<ProductionDeliveryPlanDto> UpdateEventQuantitiesAsync(int eventId, ProductionDeliveryPlanQuantitiesUpdateDto dto, CancellationToken cancellationToken = default);
    Task<ProductionDeliveryPlanDto> UpdateOrderDeliveryDateAsync(int eventId, ProductionDeliveryPlanDeliveryDateUpdateDto dto, CancellationToken cancellationToken = default);
    Task<ProductionDeliveryPlanDto> DeleteEventAsync(int eventId, CancellationToken cancellationToken = default);
    Task<ProductionDeliveryPlanDto> RemoveFromPlanAsync(int orderId, CancellationToken cancellationToken = default);
    Task<ProductionDeliveryPlanDto> AddToPlanAsync(int orderId, CancellationToken cancellationToken = default);
    Task<List<OrderExcludedFromPlanDto>> GetExcludedOrdersAsync(CancellationToken cancellationToken = default);
}