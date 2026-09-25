using MechanicAI.Application.Abstractions;
using MechanicAI.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Infrastructure.Platform;

/// <summary>Writes audit entries to the database and the structured log.</summary>
public sealed class AuditLogger(IAppDbContextFactory dbFactory, ILogger<AuditLogger> logger) : IAuditLogger
{
    /// <summary>Optional ambient identity (set by the server per request, or the desktop sign-in).</summary>
    public static readonly AsyncLocal<(Guid? UserId, string? UserName, string? Ip)> Ambient = new();

    public async Task LogAsync(string action, string? entityType = null, string? entityId = null, string? details = null,
        bool succeeded = true, CancellationToken ct = default)
    {
        var (userId, userName, ip) = Ambient.Value;
        logger.LogInformation("Audit {Action} {EntityType} {EntityId} succeeded={Succeeded} user={User}",
            action, entityType, entityId, succeeded, userName ?? "local");
        try
        {
            await using var db = await dbFactory.CreateAsync(ct);
            db.AuditLog.Add(new AuditLogEntry
            {
                Action = action,
                EntityType = entityType,
                EntityId = entityId,
                Details = details,
                Succeeded = succeeded,
                UserId = userId,
                UserName = userName,
                IpAddress = ip,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Auditing must never break the operation being audited.
            logger.LogWarning(ex, "Failed to persist audit entry {Action}", action);
        }
    }
}
