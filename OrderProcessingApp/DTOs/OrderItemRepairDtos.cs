namespace OrderProcessingApp.DTOs;

public class OrderItemSwapAuditCandidateDto
{
    public int OrderId { get; set; }
    public int OrderItemId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string SKU { get; set; } = string.Empty;
    public decimal CurrentQuantity { get; set; }
    public decimal CurrentUnitPrice { get; set; }
    public decimal CurrentLineTotal { get; set; }
    public string QtyRaw { get; set; } = string.Empty;
    public string CostperRaw { get; set; } = string.Empty;
    public string GrossCstRaw { get; set; } = string.Empty;
    public string ExetendCstRaw { get; set; } = string.Empty;
    public decimal SuggestedQuantity { get; set; }
    public decimal SuggestedUnitPrice { get; set; }
    public string DetectionReason { get; set; } = string.Empty;
    public bool IsConfirmedSwap { get; set; }
}

public class OrderItemSwapAuditResponseDto
{
    public int TotalScanned { get; set; }
    public int TotalCandidates { get; set; }
    public int TotalConfirmed { get; set; }
    public List<OrderItemSwapAuditCandidateDto> Items { get; set; } = new();
}

public class OrderItemSwapRepairResponseDto
{
    public bool DryRun { get; set; }
    public int TotalScanned { get; set; }
    public int TotalCandidates { get; set; }
    public int TotalConfirmed { get; set; }
    public int RepairedCount { get; set; }
    public int SkippedCount { get; set; }
    public List<OrderItemSwapAuditCandidateDto> RepairedItems { get; set; } = new();
    public List<OrderItemSwapAuditCandidateDto> SkippedItems { get; set; } = new();
}
