using System.ComponentModel.DataAnnotations;

namespace OrderProcessingApp.DTOs;

public class CreateMissingDistributionCentresRequestDto
{
    [MinLength(1)]
    public List<string> Centres { get; set; } = new();

    public int? DistributionCentreId { get; set; }
}

public class CreateMissingDistributionCentresResultDto
{
    public List<string> CreatedCentres { get; set; } = new();
    public List<string> ExistingCentres { get; set; } = new();
    public int DistributionCentreId { get; set; }
}

public class RetryCsvImportDto
{
    [Required]
    public string FileId { get; set; } = string.Empty;

    public bool CreateMissing { get; set; }
    public bool CreateMissingProducts { get; set; }

    public int? DistributionCentreId { get; set; }
}