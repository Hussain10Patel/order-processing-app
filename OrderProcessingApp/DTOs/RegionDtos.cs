using System.ComponentModel.DataAnnotations;

namespace OrderProcessingApp.DTOs;

public class RegionDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class CreateRegionDto
{
    [Required]
    [StringLength(120)]
    public string Name { get; set; } = string.Empty;
}
