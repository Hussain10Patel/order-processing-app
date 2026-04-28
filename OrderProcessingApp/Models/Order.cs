namespace OrderProcessingApp.Models;

public class Order
{
    public int Id { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public DateTime OrderDate { get; set; }
    public DateTime DeliveryDate { get; set; }
    public int DistributionCentreId { get; set; }
    public OrderSource Source { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public string? Notes { get; set; }
    public bool IsAdjusted { get; set; }
    public decimal TotalValue { get; set; }
    public decimal TotalPallets { get; set; }

    public DistributionCentre? DistributionCentre { get; set; }
    public ICollection<OrderItem> Items { get; set; } = new List<OrderItem>();
    public ICollection<DeliverySchedule> DeliverySchedules { get; set; } = new List<DeliverySchedule>();
}
