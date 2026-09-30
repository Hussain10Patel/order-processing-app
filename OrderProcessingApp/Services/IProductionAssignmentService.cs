using OrderProcessingApp.DTOs;

namespace OrderProcessingApp.Services;

public interface IProductionAssignmentService
{
    Task<List<ProductionAssignmentOrderDto>> GetApprovedOrdersAsync(
        string? assignment,
        IReadOnlyCollection<int>? distributionCentreIds,
        string? orderNumber,
        DateTime? orderDateFrom,
        DateTime? orderDateTo,
        DateTime? deliveryDateFrom,
        DateTime? deliveryDateTo,
        CancellationToken cancellationToken = default);

    Task<ProductionAssignmentOrderDto?> SetDeliveryDateAsync(
        int orderId,
        DateTime deliveryDate,
        CancellationToken cancellationToken = default);

    Task<ProductionAssignmentOrderDto?> UnassignAsync(
        int orderId,
        CancellationToken cancellationToken = default);
}