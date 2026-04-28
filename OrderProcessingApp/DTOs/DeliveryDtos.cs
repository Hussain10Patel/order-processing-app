using System.ComponentModel.DataAnnotations;

namespace OrderProcessingApp.DTOs;

public class ScheduleDeliveryDto
{
    [Range(1, int.MaxValue)]
    public int OrderId { get; set; }

    [Required]
    public DateTime DeliveryDate { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }
}

public class DeliveryScheduleDto
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string DistributionCentre { get; set; } = string.Empty;
    public string DeliveryDate { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public decimal TotalPallets { get; set; }
}
