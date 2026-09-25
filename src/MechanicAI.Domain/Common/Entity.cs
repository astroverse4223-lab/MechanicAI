namespace MechanicAI.Domain.Common;

/// <summary>
/// Base type for persisted entities. Ids are time-ordered (UUIDv7) so they index well
/// in both SQLite and PostgreSQL and can be generated offline without collisions,
/// which matters for syncing a desktop workstation with a shop server.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public void Touch() => UpdatedUtc = DateTime.UtcNow;
}
