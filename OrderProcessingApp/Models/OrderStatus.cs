namespace OrderProcessingApp.Models;

public enum OrderStatus
{
    Pending = 1,
    Validated = 2,
    Flagged = 3,
    Approved = 4,
    Processed = 5,
    InProduction = 6,
    Scheduled = 7,
    EnRoute = 8,
    Delivered = 9
}
