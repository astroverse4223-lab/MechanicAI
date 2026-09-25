using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MechanicAI.Application.Abstractions;
using MechanicAI.Domain.Entities;
using MechanicAI.Server.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MechanicAI.Server.Auth;

public sealed record IssuedAccessToken(string Token, string TokenId, DateTime ExpiresUtc);

public sealed record IssuedRefreshToken(string Token, string Hash, DateTime ExpiresUtc);

/// <summary>Creates signed access tokens and opaque refresh tokens (only their SHA-256 hash is stored).</summary>
public sealed class TokenService(IOptions<JwtOptions> options, IClock clock)
{
    public const string StampClaim = "sstamp";

    private static readonly JsonWebTokenHandler Handler = new();

    public IssuedAccessToken CreateAccessToken(User user)
    {
        var o = options.Value;
        var now = clock.UtcNow;
        var expires = now.AddMinutes(o.AccessTokenMinutes);
        var tokenId = Guid.CreateVersion7().ToString("N");
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = o.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(o.SigningKeyBytes), SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim(MechanicAiClaims.UserId, user.Id.ToString()),
                new Claim(MechanicAiClaims.Email, user.Email),
                new Claim(MechanicAiClaims.Name, string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email : user.DisplayName),
                new Claim(MechanicAiClaims.Role, user.Role.ToString()),
                new Claim(MechanicAiClaims.TokenId, tokenId),
                new Claim(StampClaim, StampFingerprint(user.SecurityStamp)),
            ]),
        };
        return new IssuedAccessToken(Handler.CreateToken(descriptor), tokenId, expires);
    }

    public IssuedRefreshToken CreateRefreshToken()
    {
        var token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        return new IssuedRefreshToken(token, HashRefreshToken(token), clock.UtcNow.AddDays(options.Value.RefreshTokenDays));
    }

    public static string HashRefreshToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>A short, non-reversible fingerprint of the security stamp carried in access tokens.</summary>
    public static string StampFingerprint(string securityStamp) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(securityStamp)))[..16];

    public static TokenValidationParameters CreateValidationParameters(JwtOptions o) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = o.Issuer,
        ValidateAudience = true,
        ValidAudience = o.Audience,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(o.SigningKeyBytes),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.FromSeconds(o.ClockSkewSeconds),
        NameClaimType = MechanicAiClaims.Name,
        RoleClaimType = MechanicAiClaims.Role,
    };
}

/// <summary>
/// Rejects access tokens that were revoked before they expired: signed-out tokens (a jti
/// deny-list in <see cref="IDistributedCache"/>, so it is shared through Redis when configured) and
/// tokens of users who were deactivated or changed their password (security stamp check,
/// cached briefly per user).
/// </summary>
public sealed class AccessTokenValidator(IDistributedCache cache, IMemoryCache memory, IAppDbContextFactory dbFactory, IClock clock)
{
    private static readonly TimeSpan StampCacheDuration = TimeSpan.FromSeconds(30);

    public async Task RevokeAsync(string tokenId, DateTime expiresUtc, CancellationToken ct)
    {
        var remaining = expiresUtc - clock.UtcNow;
        if (remaining <= TimeSpan.Zero) return;
        await cache.SetStringAsync(DenyKey(tokenId), "1", new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = remaining }, ct);
    }

    public async Task<string?> ValidateAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var tokenId = principal.FindFirstValue(MechanicAiClaims.TokenId);
        if (string.IsNullOrEmpty(tokenId)) return "Token has no identifier.";
        if (await cache.GetStringAsync(DenyKey(tokenId), ct) is not null) return "Token was revoked.";

        var userId = principal.GetUserId();
        if (userId is null) return "Token has no subject.";
        var state = await memory.GetOrCreateAsync(StampKey(userId.Value), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = StampCacheDuration;
            await using var db = await dbFactory.CreateAsync(ct);
            return await db.Users.AsNoTracking()
                .Where(u => u.Id == userId.Value)
                .Select(u => new UserState(u.IsActive, u.SecurityStamp))
                .FirstOrDefaultAsync(ct);
        });
        if (state is null || !state.IsActive) return "User is inactive.";
        return principal.FindFirstValue(TokenService.StampClaim) == TokenService.StampFingerprint(state.SecurityStamp)
            ? null
            : "Credentials changed since the token was issued.";
    }

    /// <summary>Forget the cached stamp after a password change, deactivation, or role change.</summary>
    public void InvalidateUser(Guid userId) => memory.Remove(StampKey(userId));

    private static string DenyKey(string tokenId) => $"mechanicai:revoked-jti:{tokenId}";

    private static string StampKey(Guid userId) => $"mechanicai:user-stamp:{userId:N}";

    private sealed record UserState(bool IsActive, string SecurityStamp);
}
