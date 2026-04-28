using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;
using OrderProcessingApp.DTOs;
using OrderProcessingApp.Models;

namespace OrderProcessingApp.Services;

public class AuditService : IAuditService
{
    private const int AuditValueMaxLength = 500;
    private readonly AppDbContext _dbContext;

    public AuditService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public void TrackChange(string entity, int entityId, string field, string? oldValue, string? newValue, string changedBy = "System")
    {
        var truncatedOldValue = Truncate(oldValue);
        var truncatedNewValue = Truncate(newValue);

        _dbContext.AuditLogs.Add(new AuditLog
        {
            Entity = entity,
            EntityId = entityId,
            Field = field,
            OldValue = truncatedOldValue,
            NewValue = truncatedNewValue,
            ChangedBy = changedBy,
            CreatedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified)
        });
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length > AuditValueMaxLength
            ? value[..AuditValueMaxLength]
            : value;
    }

    public async Task LogChangeAsync(string entity, int entityId, string field, string? oldValue, string? newValue, string changedBy = "System", CancellationToken cancellationToken = default)
    {
        TrackChange(entity, entityId, field, oldValue, newValue, changedBy);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<AuditLogDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.AuditLogs
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new AuditLogDto
            {
                Id = x.Id,
                Entity = x.Entity,
                EntityId = x.EntityId,
                Field = x.Field,
                OldValue = x.OldValue,
                NewValue = x.NewValue,
                ChangedBy = x.ChangedBy,
                CreatedAt = DateTime.SpecifyKind(x.CreatedAt, DateTimeKind.Utc).ToUniversalTime().ToString("o")
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<List<AuditLogDto>> GetByEntityAsync(string entity, int entityId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.AuditLogs
            .AsNoTracking()
            .Where(x => x.Entity == entity && x.EntityId == entityId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new AuditLogDto
            {
                Id = x.Id,
                Entity = x.Entity,
                EntityId = x.EntityId,
                Field = x.Field,
                OldValue = x.OldValue,
                NewValue = x.NewValue,
                ChangedBy = x.ChangedBy,
                CreatedAt = DateTime.SpecifyKind(x.CreatedAt, DateTimeKind.Utc).ToUniversalTime().ToString("o")
            })
            .ToListAsync(cancellationToken);
    }
}
