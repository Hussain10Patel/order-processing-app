namespace OrderProcessingApp.Models;

public class DeliverySchedule
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public DateTime DeliveryDate { get; set; }
    public string Status { get; set; } = "Scheduled";
    public string? Notes { get; set; }

    public Order? Order { get; set; }
}
