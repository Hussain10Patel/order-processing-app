using OrderProcessingApp.DTOs;

namespace OrderProcessingApp.Services;

public interface IOrderService
{
    Task<OrderDto> CreateManualOrderAsync(ManualOrderCreateDto dto, CancellationToken cancellationToken = default);
    Task<CsvImportProcessingResult> CreateOrdersFromCsvRowsAsync(List<CsvOrderRowDto> rows, bool allowDuplicates = false, bool createMissingProducts = false, CancellationToken cancellationToken = default);
    Task<CreateMissingDistributionCentresResultDto> CreateMissingDistributionCentresAsync(CreateMissingDistributionCentresRequestDto dto, CancellationToken cancellationToken = default);
    Task<List<OrderDto>> GetOrdersAsync(CancellationToken cancellationToken = default);
    Task<List<OrderDto>> GetFilteredOrdersAsync(OrderFilterDto filter, CancellationToken cancellationToken = default);
    Task<OrderDto?> GetOrderByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<OrderDto?> ProcessOrderAsync(int id, CancellationToken cancellationToken = default);
    Task<OrderDto?> ApproveOrderAsync(int id, CancellationToken cancellationToken = default);
    Task<OrderDto?> AdjustOrderAsync(int id, AdjustOrderDto dto, CancellationToken cancellationToken = default);
    Task<OrderDto?> RecalculateOrderAsync(int id, CancellationToken cancellationToken = default);
}
