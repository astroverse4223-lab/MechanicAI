using MechanicAI.Domain.Common;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.Interfaces;

namespace MechanicAI.Domain.Entities;

/// <summary>A login account. Passwords are only ever stored as salted PBKDF2 hashes.</summary>
public class User : Entity, IAggregateRoot
{
    public string Email { get; set; } = string.Empty;

    public string NormalizedEmail { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.Technician;

    public bool IsActive { get; set; } = true;

    public int FailedLoginCount { get; set; }

    public DateTime? LockoutEndUtc { get; set; }

    public DateTime? LastLoginUtc { get; set; }

    /// <summary>Changes whenever credentials change, invalidating outstanding refresh tokens.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    public Guid? TechnicianId { get; set; }

    public Technician? Technician { get; set; }

    public List<RefreshToken> RefreshTokens { get; set; } = [];

    public bool IsLockedOut(DateTime nowUtc) => LockoutEndUtc is { } end && end > nowUtc;
}

/// <summary>Server-side refresh token. Only a SHA-256 hash of the token is stored.</summary>
public class RefreshToken : Entity
{
    public Guid UserId { get; set; }

    public User? User { get; set; }

    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresUtc { get; set; }

    public DateTime? RevokedUtc { get; set; }

    public string? ReplacedByTokenHash { get; set; }

    public string? CreatedByIp { get; set; }

    public bool IsActive(DateTime nowUtc) => RevokedUtc is null && ExpiresUtc > nowUtc;
}

/// <summary>A technician or apprentice working in the shop.</summary>
public class Technician : Entity, IAggregateRoot, ISampleData
{
    public string Name { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string? Phone { get; set; }

    /// <summary>Free-form certifications, e.g. "ASE A1, A6, A8, L1".</summary>
    public string? Certifications { get; set; }

    public string? Specialties { get; set; }

    public bool IsApprentice { get; set; }

    /// <summary>Holds hybrid/EV high-voltage safety training. Used to tailor HV warnings.</summary>
    public bool HighVoltageCertified { get; set; }

    public bool IsActive { get; set; } = true;

    public bool IsSample { get; set; }
}

/// <summary>Append-only audit trail for security-relevant and data-changing actions.</summary>
public class AuditLogEntry
{
    public long Id { get; set; }

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public Guid? UserId { get; set; }

    public string? UserName { get; set; }

    public string Action { get; set; } = string.Empty;

    public string? EntityType { get; set; }

    public string? EntityId { get; set; }

    /// <summary>Additional context. Must never contain passwords, tokens, or API keys.</summary>
    public string? Details { get; set; }

    public string? IpAddress { get; set; }

    public bool Succeeded { get; set; } = true;
}
