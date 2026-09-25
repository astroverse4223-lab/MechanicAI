using System.Security.Claims;
using MechanicAI.Domain.Enums;
using Microsoft.AspNetCore.Authorization;

namespace MechanicAI.Server.Auth;

/// <summary>
/// Authorization policies mapped onto <see cref="UserRole"/>. Every endpoint requires an
/// authenticated user by default (fallback policy); these narrow write access by role.
/// </summary>
public static class Policies
{
    /// <summary>User management and shop administration: Owner, Admin.</summary>
    public const string Admin = "Admin";

    /// <summary>Running diagnostic sessions and training: everyone who works on vehicles, including apprentices.</summary>
    public const string Diagnostics = "Diagnostics";

    /// <summary>Customer, vehicle, estimate, and inspection records: technicians and service advisors.</summary>
    public const string Records = "Records";

    /// <summary>Changing the shared knowledge base and DTC reference: technicians and above.</summary>
    public const string Knowledge = "Knowledge";

    public static readonly IReadOnlyDictionary<string, UserRole[]> Roles = new Dictionary<string, UserRole[]>
    {
        [Admin] = [UserRole.Owner, UserRole.Admin],
        [Diagnostics] = [UserRole.Owner, UserRole.Admin, UserRole.Technician, UserRole.Apprentice],
        [Records] = [UserRole.Owner, UserRole.Admin, UserRole.Technician, UserRole.ServiceAdvisor],
        [Knowledge] = [UserRole.Owner, UserRole.Admin, UserRole.Technician],
    };

    public static AuthorizationBuilder AddMechanicAiPolicies(this AuthorizationBuilder builder)
    {
        builder.SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        foreach (var (name, roles) in Roles)
        {
            builder.AddPolicy(name, p => p.RequireAuthenticatedUser().RequireRole(roles.Select(r => r.ToString())));
        }

        return builder;
    }
}

/// <summary>Claim names used in access tokens.</summary>
public static class MechanicAiClaims
{
    public const string UserId = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string Role = "role";
    public const string TokenId = "jti";

    public static Guid? GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(UserId), out var id) ? id : null;

    public static string? GetDisplayName(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(Name) ?? principal.FindFirstValue(Email);
}
