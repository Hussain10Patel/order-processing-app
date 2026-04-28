using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace OrderProcessingApp.Models;

public class OrderItem
{
    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web);

    public int Id { get; set; }
    public int OrderId { get; set; }
    public int ProductId { get; set; }
    public string? ProductCode { get; set; }
    public string? ProductName { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Pallets { get; set; }
    public bool IsUnmapped { get; set; }
    public bool IsPriceMissing { get; set; }
    public bool IsPriceMismatch { get; set; }
    public bool IsCsvPrice { get; set; }
    public string MetadataJson { get; set; } = "{}";

    [NotMapped]
    public Dictionary<string, string> Metadata
    {
        get => JsonSerializer.Deserialize<Dictionary<string, string>>(MetadataJson, MetadataJsonOptions) ?? new Dictionary<string, string>();
        set => MetadataJson = JsonSerializer.Serialize(value ?? new Dictionary<string, string>(), MetadataJsonOptions);
    }

    public Order? Order { get; set; }
    public Product? Product { get; set; }
}
