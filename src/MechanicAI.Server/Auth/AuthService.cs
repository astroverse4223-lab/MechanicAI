using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Application.Services;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using MechanicAI.Infrastructure.Platform;
using MechanicAI.Server.Contracts;
using MechanicAI.Server.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MechanicAI.Server.Auth;

/// <summary>
/// Shop-server accounts: sign-in with lockout and transparent hash upgrades, rotating refresh
/// tokens with reuse detection, and user administration. Every security event is audited.
/// </summary>
public sealed class AuthService(
    IAppDbContextFactory dbFactory,
    IPasswordHasher hasher,
    TokenService tokens,
    AccessTokenValidator accessTokens,
    IAuditLogger audit,
    IClock clock,
    IOptions<AuthOptions> authOptions,
    ILogger<AuthService> logger)
{
    private const string InvalidCredentials = "Invalid email or password.";
    private const string InvalidRefreshToken = "The session has expired or was revoked. Sign in again.";

    private readonly Lazy<string> _timingDummyHash = new(() => hasher.Hash("timing-equalization-only"));

    public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();

    public async Task<Result<TokenResponse>> LoginAsync(string? email, string? password, string? ip, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password)) return new Error(ErrorKind.Unauthorized, InvalidCredentials);

        var now = clock.UtcNow;
        var normalized = NormalizeEmail(email);
        await using var db = await dbFactory.CreateAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);
        if (user is null)
        {
            // Spend the same time as a real verification so response timing does not reveal accounts.
            hasher.Verify(_timingDummyHash.Value, password);
            await AuditAsAsync(null, email, ip, "auth.login", succeeded: false, details: "unknown account", ct);
            return new Error(ErrorKind.Unauthorized, InvalidCredentials);
        }

        if (user.IsLockedOut(now))
        {
            await AuditAsAsync(user.Id, user.Email, ip, "auth.login", succeeded: false, details: "locked out", ct);
            return new Error(ErrorKind.RateLimited, "Too many failed sign-in attempts. Try again later.");
        }

        var verification = hasher.Verify(user.PasswordHash, password);
        if (verification == PasswordVerification.Failed || !user.IsActive)
        {
            if (verification == PasswordVerification.Failed)
            {
                user.FailedLoginCount++;
                if (user.FailedLoginCount >= Math.Max(1, authOptions.Value.MaxFailedAttempts))
                {
                    user.LockoutEndUtc = now.AddMinutes(authOptions.Value.LockoutMinutes);
                    user.FailedLoginCount = 0;
                    logger.LogWarning("Account {UserId} locked out after repeated failed sign-ins", user.Id);
                }

                await db.SaveChangesAsync(ct);
            }

            await AuditAsAsync(user.Id, user.Email, ip, "auth.login", succeeded: false, details: user.IsActive ? "bad password" : "inactive account", ct);
            return new Error(ErrorKind.Unauthorized, InvalidCredentials);
        }

        if (verification == PasswordVerification.SuccessRehashNeeded) user.PasswordHash = hasher.Hash(password);
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        user.LastLoginUtc = now;
        var response = IssueTokens(db, user, ip);
        await db.SaveChangesAsync(ct);
        await AuditAsAsync(user.Id, user.Email, ip, "auth.login", succeeded: true,
            details: verification == PasswordVerification.SuccessRehashNeeded ? "password hash upgraded" : null, ct);
        return response;
    }

    public async Task<Result<TokenResponse>> RefreshAsync(string? refreshToken, string? ip, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return new Error(ErrorKind.Unauthorized, InvalidRefreshToken);
        var now = clock.UtcNow;
        var hash = TokenService.HashRefreshToken(refreshToken);
        await using var db = await dbFactory.CreateAsync(ct);
        var stored = await db.RefreshTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored?.User is null) return new Error(ErrorKind.Unauthorized, InvalidRefreshToken);
        var user = stored.User;

        if (stored.RevokedUtc is not null)
        {
            await RevokeFamilyAsync(db, stored, now, ct);
            await AuditAsAsync(user.Id, user.Email, ip, "auth.refresh.reuse", succeeded: false, details: "revoked refresh token presented; token family revoked", ct);
            logger.LogWarning("Refresh token reuse detected for user {UserId}; token family revoked", user.Id);
            return new Error(ErrorKind.Unauthorized, InvalidRefreshToken);
        }

        if (stored.ExpiresUtc <= now || !user.IsActive || user.IsLockedOut(now))
        {
            stored.RevokedUtc ??= now;
            await db.SaveChangesAsync(ct);
            await AuditAsAsync(user.Id, user.Email, ip, "auth.refresh", succeeded: false, details: stored.ExpiresUtc <= now ? "expired" : "account unavailable", ct);
            return new Error(ErrorKind.Unauthorized, InvalidRefreshToken);
        }

        var next = tokens.CreateRefreshToken();

        // Atomic rotation: exactly one caller can consume a refresh token. A concurrent (or
        // replayed) second use finds it already revoked and is treated as reuse.
        var rotated = await db.RefreshTokens
            .Where(t => t.Id == stored.Id && t.RevokedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedUtc, now).SetProperty(t => t.ReplacedByTokenHash, next.Hash), ct);
        if (rotated == 0)
        {
            var current = await db.RefreshTokens.AsNoTracking().FirstAsync(t => t.Id == stored.Id, ct);
            await RevokeFamilyAsync(db, current, now, ct);
            await AuditAsAsync(user.Id, user.Email, ip, "auth.refresh.reuse", succeeded: false, details: "concurrent refresh token use; token family revoked", ct);
            return new Error(ErrorKind.Unauthorized, InvalidRefreshToken);
        }

        var access = tokens.CreateAccessToken(user);
        db.RefreshTokens.Add(new RefreshToken { UserId = user.Id, TokenHash = next.Hash, ExpiresUtc = next.ExpiresUtc, CreatedByIp = ip });
        await db.SaveChangesAsync(ct);
        await AuditAsAsync(user.Id, user.Email, ip, "auth.refresh", succeeded: true, details: null, ct);
        return new TokenResponse(access.Token, access.ExpiresUtc, next.Token, next.ExpiresUtc, UserDto.From(user));
    }

    /// <summary>Revokes the presented refresh token (if any) and the caller's current access token.</summary>
    public async Task LogoutAsync(string? refreshToken, Guid? userId, string? accessTokenId, DateTime? accessTokenExpiresUtc, CancellationToken ct)
    {
        var now = clock.UtcNow;
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            var hash = TokenService.HashRefreshToken(refreshToken);
            await using var db = await dbFactory.CreateAsync(ct);
            var query = db.RefreshTokens.Where(t => t.TokenHash == hash && t.RevokedUtc == null);
            if (userId is { } id) query = query.Where(t => t.UserId == id);
            await query.ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedUtc, now), ct);
        }

        if (accessTokenId is not null && accessTokenExpiresUtc is { } expires) await accessTokens.RevokeAsync(accessTokenId, expires, ct);
        await audit.LogAsync("auth.logout", "User", userId?.ToString(), ct: ct);
    }

    public async Task<User?> GetUserAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
    }

    public async Task<Result> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Error.NotFound("User");
        if (hasher.Verify(user.PasswordHash, currentPassword ?? string.Empty) == PasswordVerification.Failed)
        {
            await audit.LogAsync("auth.password.change", "User", user.Id.ToString(), succeeded: false, ct: ct);
            return Error.Validation("The current password is incorrect.");
        }

        var problem = ValidatePassword(newPassword);
        if (problem is not null) return Error.Validation(problem);
        user.PasswordHash = hasher.Hash(newPassword);
        await InvalidateSessionsAsync(db, user, ct);
        await audit.LogAsync("auth.password.change", "User", user.Id.ToString(), ct: ct);
        return Result.Success();
    }

    // ------------------------------------------------------------------ administration

    public async Task<IReadOnlyList<User>> ListUsersAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ThenBy(u => u.Email).ToListAsync(ct);
    }

    public async Task<Result<User>> CreateUserAsync(CreateUserRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@', StringComparison.Ordinal))
        {
            return Error.Validation("Enter a valid email address.");
        }

        var problem = ValidatePassword(request.Password);
        if (problem is not null) return Error.Validation(problem);
        if (!Enum.IsDefined(request.Role)) return Error.Validation("Unknown role.");

        await using var db = await dbFactory.CreateAsync(ct);
        var normalized = NormalizeEmail(request.Email);
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalized, ct)) return new Error(ErrorKind.Conflict, "A user with this email already exists.");

        var user = new User
        {
            Email = request.Email.Trim(),
            NormalizedEmail = normalized,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? request.Email.Trim() : request.DisplayName.Trim(),
            PasswordHash = hasher.Hash(request.Password),
            Role = request.Role,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("user.create", "User", user.Id.ToString(), $"role={user.Role}", ct: ct);
        return user;
    }

    public async Task<Result<User>> UpdateUserAsync(Guid id, UpdateUserRequest request, Guid actingUserId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return Error.NotFound("User");

        var credentialsChanged = false;
        if (request.Role is { } role && role != user.Role)
        {
            if (!Enum.IsDefined(role)) return Error.Validation("Unknown role.");
            if (id == actingUserId) return Error.Validation("You cannot change your own role.");
            if (user.Role == UserRole.Owner && !await AnotherActiveOwnerExistsAsync(db, id, ct)) return new Error(ErrorKind.Conflict, "The shop must keep at least one active owner.");
            user.Role = role;
            credentialsChanged = true;
        }

        if (request.IsActive is { } active && active != user.IsActive)
        {
            if (!active && id == actingUserId) return Error.Validation("You cannot deactivate your own account.");
            if (!active && user.Role == UserRole.Owner && !await AnotherActiveOwnerExistsAsync(db, id, ct)) return new Error(ErrorKind.Conflict, "The shop must keep at least one active owner.");
            user.IsActive = active;
            credentialsChanged = true;
        }

        if (!string.IsNullOrWhiteSpace(request.DisplayName)) user.DisplayName = request.DisplayName.Trim();
        if (credentialsChanged)
        {
            await InvalidateSessionsAsync(db, user, ct);
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }

        await audit.LogAsync("user.update", "User", user.Id.ToString(), $"role={user.Role}; active={user.IsActive}", ct: ct);
        return user;
    }

    public async Task<Result> ResetPasswordAsync(Guid id, string newPassword, CancellationToken ct)
    {
        var problem = ValidatePassword(newPassword);
        if (problem is not null) return Error.Validation(problem);
        await using var db = await dbFactory.CreateAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return Error.NotFound("User");
        user.PasswordHash = hasher.Hash(newPassword);
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        await InvalidateSessionsAsync(db, user, ct);
        await audit.LogAsync("user.password.reset", "User", user.Id.ToString(), ct: ct);
        return Result.Success();
    }

    /// <summary>
    /// Creates the first Owner. Refused once any Owner/Admin exists, so it can never be used to
    /// take over a configured server.
    /// </summary>
    public async Task<Result<User>> BootstrapOwnerAsync(string email, string password, string displayName, CancellationToken ct)
    {
        await using (var db = await dbFactory.CreateAsync(ct))
        {
            if (await db.Users.AnyAsync(u => u.Role == UserRole.Owner || u.Role == UserRole.Admin, ct))
            {
                return new Error(ErrorKind.Conflict, "An administrator account already exists; bootstrap is disabled.");
            }
        }

        var created = await CreateUserAsync(new CreateUserRequest(email, displayName, password, UserRole.Owner), ct);
        if (created.IsSuccess) await audit.LogAsync("user.bootstrap", "User", created.Value!.Id.ToString(), ct: ct);
        return created;
    }

    public string? ValidatePassword(string? password)
    {
        var min = Math.Max(WorkstationAuthService.MinimumPasswordLength, authOptions.Value.MinPasswordLength);
        if (string.IsNullOrEmpty(password) || password.Length < min) return $"Use at least {min} characters.";
        if (password.Length > 256) return "Use at most 256 characters.";
        return WorkstationAuthService.ValidateStrength(password);
    }

    private TokenResponse IssueTokens(IAppDbContext db, User user, string? ip)
    {
        var access = tokens.CreateAccessToken(user);
        var refresh = tokens.CreateRefreshToken();
        db.RefreshTokens.Add(new RefreshToken { UserId = user.Id, TokenHash = refresh.Hash, ExpiresUtc = refresh.ExpiresUtc, CreatedByIp = ip });
        return new TokenResponse(access.Token, access.ExpiresUtc, refresh.Token, refresh.ExpiresUtc, UserDto.From(user));
    }

    /// <summary>New security stamp (invalidates access tokens) and every refresh token revoked.</summary>
    private async Task InvalidateSessionsAsync(IAppDbContext db, User user, CancellationToken ct)
    {
        var now = clock.UtcNow;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync(ct);
        await db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedUtc, now), ct);
        accessTokens.InvalidateUser(user.Id);
    }

    /// <summary>Revokes every token rotated from <paramref name="token"/> (the descendants a thief or the victim may hold).</summary>
    private static async Task RevokeFamilyAsync(IAppDbContext db, RefreshToken token, DateTime now, CancellationToken ct)
    {
        var next = token.ReplacedByTokenHash;
        var guard = 0;
        while (next is not null && guard++ < 10_000)
        {
            var descendant = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == next, ct);
            if (descendant is null) break;
            descendant.RevokedUtc ??= now;
            next = descendant.ReplacedByTokenHash;
        }

        await db.SaveChangesAsync(ct);
    }

    private static Task<bool> AnotherActiveOwnerExistsAsync(IAppDbContext db, Guid excludingId, CancellationToken ct) =>
        db.Users.AnyAsync(u => u.Id != excludingId && u.Role == UserRole.Owner && u.IsActive, ct);

    /// <summary>Sign-in happens before the request is authenticated, so attribute the audit entry explicitly.</summary>
    private async Task AuditAsAsync(Guid? userId, string? userName, string? ip, string action, bool succeeded, string? details, CancellationToken ct)
    {
        var previous = AuditLogger.Ambient.Value;
        AuditLogger.Ambient.Value = (userId, userName, ip);
        try
        {
            await audit.LogAsync(action, "User", userId?.ToString(), details, succeeded, ct);
        }
        finally
        {
            AuditLogger.Ambient.Value = previous;
        }
    }
}
