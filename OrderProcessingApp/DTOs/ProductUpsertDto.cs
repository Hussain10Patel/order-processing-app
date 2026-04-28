using System.ComponentModel.DataAnnotations;

namespace OrderProcessingApp.DTOs;

public class ProductUpsertDto
{
    [Required]
    [StringLength(120)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(50)]
    public string SKUCode { get; set; } = string.Empty;

    [Range(0.0001, 999999999.0)]
    public decimal PalletConversionRate { get; set; }
}
