namespace OrderProcessingApp.DTOs;

public class CreateDistributionCentreDto
{
    public string Name { get; set; } = string.Empty;
    public int? DistributionCentreId { get; set; }
}

public class DistributionCentreDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int DistributionCentreId { get; set; }
}