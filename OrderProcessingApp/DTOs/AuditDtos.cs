namespace OrderProcessingApp.DTOs;

public class AuditLogDto
{
    public int Id { get; set; }
    public string Entity { get; set; } = string.Empty;
    public int EntityId { get; set; }
    public string Field { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string ChangedBy { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
}
