namespace OrderProcessingApp.Models;

public class Region
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public ICollection<DistributionCentre> DistributionCentres { get; set; } = new List<DistributionCentre>();
}
