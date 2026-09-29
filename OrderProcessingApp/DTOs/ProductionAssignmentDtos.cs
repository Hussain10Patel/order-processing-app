using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrderProcessingApp.DTOs;

public sealed class ProductionAssignmentOrderDto
{
    public int Id { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public int DistributionCentreId { get; set; }
    public string DistributionCentreName { get; set; } = string.Empty;
    public string OrderDate { get; set; } = string.Empty;
    public string? DeliveryDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsScheduled { get; set; }
    public bool IsAssigned => DeliveryDate is not null;
}

public sealed class SetOrderDeliveryDateDto
{
    private DateTime? _deliveryDate;

    [JsonPropertyName("deliveryDate")]
    public DateTime? DeliveryDate
    {
        get => _deliveryDate;
        set
        {
            _deliveryDate = value;
            HasDeliveryDate = true;
        }
    }

    [JsonIgnore]
    public bool HasDeliveryDate { get; private set; }
}