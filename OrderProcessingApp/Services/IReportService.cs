using OrderProcessingApp.DTOs;

namespace OrderProcessingApp.Services;

public interface IReportService
{
    Task<ReportSummaryDto> GetSummaryByDeliveryDateAsync(DateTime date, CancellationToken cancellationToken = default);
    Task<List<SupplierSummaryGroupDto>> GetSupplierSummaryAsync(DateTime date, CancellationToken cancellationToken = default);
    Task<List<SupplierSummaryItemDto>> GetSupplierDeliveryAsync(DateTime? date, CancellationToken cancellationToken = default);
    Task<List<DailyDeliveryGroupDto>> GetDailyDeliveryReportAsync(DateTime date, CancellationToken cancellationToken = default);
    Task<OrdersReportDto> GetOrdersReportAsync(CancellationToken cancellationToken = default);
    Task<SalesReportDto> GetSalesSummaryAsync(CancellationToken cancellationToken = default);
}
