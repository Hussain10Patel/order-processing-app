using OrderProcessingApp.DTOs;

namespace OrderProcessingApp.Services;

public interface IDeliveryService
{
    Task<DeliveryScheduleDto> ScheduleDeliveryAsync(int orderId, DateTime deliveryDate, string? notes, CancellationToken cancellationToken = default);
    Task<List<DeliveryScheduleDto>> GetScheduleByDateAsync(DateTime date, CancellationToken cancellationToken = default);
    Task<List<OrderDto>> GetUnscheduledOrdersByDateAsync(DateTime date, CancellationToken cancellationToken = default);
}
