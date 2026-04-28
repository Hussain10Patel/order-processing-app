using System.ComponentModel.DataAnnotations;

namespace OrderProcessingApp.DTOs;

public class ManualOrderCreateDto
{
    [Required]
    [StringLength(50)]
    public string OrderNumber { get; set; } = string.Empty;

    [Required]
    public DateTime OrderDate { get; set; }

    [Required]
    public DateTime DeliveryDate { get; set; }

    [Range(1, int.MaxValue)]
    public int DistributionCentreId { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }

    [MinLength(1)]
    public List<OrderItemCreateDto> Items { get; set; } = new();
}
