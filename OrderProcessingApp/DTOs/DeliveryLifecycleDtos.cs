using System.ComponentModel.DataAnnotations;

namespace OrderProcessingApp.DTOs;

public sealed class SetEnRouteDto
{
    [Required]
    public decimal? DurationHours { get; set; }
}

public sealed class DeliveryLifecycleDto
{
    public int OrderId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset? EnRouteAtUtc { get; set; }
    public decimal? ExpectedDeliveryDurationHours { get; set; }
    public DateTimeOffset? ExpectedDeliveryAtUtc { get; set; }
    public DateTimeOffset? DeliveredAtUtc { get; set; }
    public bool IsDeliveryEstimated { get; set; }
}
