using System.Text;
using MechanicAI.Infrastructure;

namespace MechanicAI.Server.Hosting;

/// <summary><c>Database</c> section.</summary>
public sealed class DatabaseOptions
{
    public const string Section = "Database";

    /// <summary>Postgres (production) or Sqlite (single-box evaluation and integration tests).</summary>
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Postgres;

    /// <summary>Apply EF Core migrations and seed reference content at startup.</summary>
    public bool ApplyMigrationsOnStartup { get; set; } = true;
}

/// <summary><c>Storage</c> section.</summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>Directory for uploaded documents and server-side settings. Mount a volume here in containers.</summary>
    public string? DataRoot { get; set; }
}

/// <summary><c>Jwt</c> section. The signing key is a secret: supply it through an environment variable or user-secrets.</summary>
public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public const int MinimumKeyBytes = 32;

    public string Issuer { get; set; } = "MechanicAI.Server";

    public string Audience { get; set; } = "MechanicAI.Workstation";

    public string? SigningKey { get; set; }

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 14;

    /// <summary>Tolerated clock difference between the server and token consumers.</summary>
    public int ClockSkewSeconds { get; set; } = 30;

    public byte[] SigningKeyBytes => Encoding.UTF8.GetBytes(SigningKey ?? string.Empty);
}

/// <summary><c>Auth</c> section: sign-in hardening.</summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    public int MaxFailedAttempts { get; set; } = 5;

    public int LockoutMinutes { get; set; } = 15;

    public int MinPasswordLength { get; set; } = 12;
}

/// <summary><c>Bootstrap</c> section: creates the first Owner account when the user table is empty.</summary>
public sealed class BootstrapOptions
{
    public const string Section = "Bootstrap";

    public string? AdminEmail { get; set; }

    public string? AdminPassword { get; set; }

    public string AdminDisplayName { get; set; } = "Shop owner";
}

/// <summary><c>RateLimiting</c> section.</summary>
public sealed class RateLimitingOptions
{
    public const string Section = "RateLimiting";

    public WindowLimit Auth { get; set; } = new() { PermitLimit = 10, WindowSeconds = 60 };

    public WindowLimit Api { get; set; } = new() { PermitLimit = 600, WindowSeconds = 60 };

    public sealed class WindowLimit
    {
        public int PermitLimit { get; set; }

        public int WindowSeconds { get; set; }
    }
}

/// <summary><c>Uploads</c> section.</summary>
public sealed class UploadOptions
{
    public const string Section = "Uploads";

    public long MaxDocumentBytes { get; set; } = 100L * 1024 * 1024;
}

/// <summary><c>ForwardedHeaders</c> section (running behind a reverse proxy).</summary>
public sealed class ForwardedHeadersSettings
{
    public const string Section = "ForwardedHeaders";

    public bool Enabled { get; set; }

    public List<string> KnownProxies { get; set; } = [];

    /// <summary>CIDR ranges, e.g. 10.0.0.0/8.</summary>
    public List<string> KnownNetworks { get; set; } = [];
}

/// <summary><c>Https</c> section.</summary>
public sealed class HttpsSettings
{
    public const string Section = "Https";

    public bool Redirect { get; set; }

    public bool Hsts { get; set; }
}

/// <summary><c>Cors</c> section. CORS is off unless origins are listed (workstations are not browsers).</summary>
public sealed class CorsSettings
{
    public const string Section = "Cors";

    public List<string> AllowedOrigins { get; set; } = [];
}
