namespace OrderProcessingApp.Options;

public class CsvImportOptions
{
    public Dictionary<string, string> DistributionCentreAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}