namespace OrderProcessingApp.DTOs;

public class SupplierSummaryItemDto
{
    public string OrderNumber { get; set; } = string.Empty;
    public string OrderDate { get; set; } = string.Empty;
    public string DistributionCentre { get; set; } = string.Empty;
    public string DeliveryDate { get; set; } = string.Empty;
    public decimal TotalPallets { get; set; }
}

public class SupplierSummaryGroupDto
{
    public string DistributionCentre { get; set; } = string.Empty;
    public decimal TotalPallets { get; set; }
    public List<SupplierSummaryItemDto> Orders { get; set; } = new();
}

public class DailyDeliveryOrderDto
{
    public string OrderNumber { get; set; } = string.Empty;
    public List<string> ProductSummary { get; set; } = new();
    public decimal TotalPallets { get; set; }
}

public class DailyDeliveryGroupDto
{
    public string DistributionCentre { get; set; } = string.Empty;
    public List<DailyDeliveryOrderDto> Deliveries { get; set; } = new();
}

// ── Orders summary report ──────────────────────────────────────────────────

public class OrdersReportDto
{
    public int TotalOrders { get; set; }
    public decimal TotalValue { get; set; }
    public List<OrdersByStatusDto> ByStatus { get; set; } = new();
    public List<OrdersByDcDto> ByDistributionCentre { get; set; } = new();
}

public class OrdersByStatusDto
{
    public string Status { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal TotalValue { get; set; }
}

public class OrdersByDcDto
{
    public string DistributionCentre { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal TotalValue { get; set; }
}

// ── Sales summary report ───────────────────────────────────────────────────

public class SalesReportDto
{
    public decimal TotalRevenue { get; set; }
    public List<SalesByProductDto> ByProduct { get; set; } = new();
    public List<SalesByDcDto> ByDistributionCentre { get; set; } = new();
}

public class SalesByProductDto
{
    public string ProductName { get; set; } = string.Empty;
    public string SKUCode { get; set; } = string.Empty;
    public decimal TotalQuantity { get; set; }
    public decimal TotalRevenue { get; set; }
    public decimal TotalPallets { get; set; }
}

public class SalesByDcDto
{
    public string DistributionCentre { get; set; } = string.Empty;
    public int TotalOrders { get; set; }
    public decimal TotalRevenue { get; set; }
}

public class ReportSummaryDto
{
    public int TotalOrders { get; set; }
    public decimal TotalValue { get; set; }
    public List<ReportStatusCountDto> OrdersByStatus { get; set; } = new();
    public List<ReportSalesByProductSummaryDto> SalesByProduct { get; set; } = new();
    public List<ReportDeliverySummaryDto> DeliverySummary { get; set; } = new();
}

public class ReportStatusCountDto
{
    public string Status { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal TotalValue { get; set; }
}

public class ReportSalesByProductSummaryDto
{
    public string Product { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal Value { get; set; }
}

public class ReportDeliverySummaryDto
{
    public string PoNumber { get; set; } = string.Empty;
    public string Dc { get; set; } = string.Empty;
    public string DeliveryDate { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

