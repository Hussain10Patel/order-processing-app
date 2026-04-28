using OrderProcessingApp.DTOs;

namespace OrderProcessingApp.Services;

public interface IAuditService
{
    /// <summary>Adds an audit entry to the current DbContext unit-of-work (caller must SaveChanges).</summary>
    void TrackChange(string entity, int entityId, string field, string? oldValue, string? newValue, string changedBy = "System");

    /// <summary>Persists a single audit entry immediately.</summary>
    Task LogChangeAsync(string entity, int entityId, string field, string? oldValue, string? newValue, string changedBy = "System", CancellationToken cancellationToken = default);

    Task<List<AuditLogDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<List<AuditLogDto>> GetByEntityAsync(string entity, int entityId, CancellationToken cancellationToken = default);
}
