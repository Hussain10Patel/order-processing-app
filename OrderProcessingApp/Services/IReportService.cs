using OrderProcessingApp.DTOs;

namespace OrderProcessingApp.Services;

public interface IReportService
{
    Task<List<ReportAvailableDateDto>> GetAvailableReportDatesAsync(CancellationToken cancellationToken = default);
    Task<ReportSummaryDto> GetSummaryByDeliveryDateAsync(DateTime date, CancellationToken cancellationToken = default);
    Task<ReportSummaryDto> GetSummaryByDeliveryDateRangeAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
    Task<List<SupplierSummaryGroupDto>> GetSupplierSummaryAsync(DateTime date, CancellationToken cancellationToken = default);
    Task<List<SupplierSummaryItemDto>> GetSupplierDeliveryAsync(DateTime? date, CancellationToken cancellationToken = default);
    Task<List<DailyDeliveryGroupDto>> GetDailyDeliveryReportAsync(DateTime date, CancellationToken cancellationToken = default);
    Task<OrdersReportDto> GetOrdersReportAsync(CancellationToken cancellationToken = default);
    Task<SalesReportDto> GetSalesSummaryAsync(CancellationToken cancellationToken = default);
}
