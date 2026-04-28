namespace OrderProcessingApp.Models;

public class AuditLog
{
    public int Id { get; set; }
    public string Entity { get; set; } = string.Empty;
    public int EntityId { get; set; }
    public string Field { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string ChangedBy { get; set; } = "System";
    public DateTime CreatedAt { get; set; }
}
