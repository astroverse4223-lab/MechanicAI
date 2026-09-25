using System.Security.Claims;
using MechanicAI.Infrastructure.Platform;
using MechanicAI.Server.Auth;
using Serilog.Context;

namespace MechanicAI.Server.Http;

/// <summary>
/// Establishes the per-request identity used by <see cref="AuditLogger"/> (user id, name, client IP)
/// and pushes the user id into the log context. Runs after authentication.
/// </summary>
public sealed class AuditContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var user = context.User;
        var userId = user.Identity?.IsAuthenticated == true ? user.GetUserId() : null;
        var name = userId is null ? null : user.GetDisplayName();
        var ip = context.GetClientIp();

        var previous = AuditLogger.Ambient.Value;
        AuditLogger.Ambient.Value = (userId, name, ip);
        try
        {
            using (LogContext.PushProperty("UserId", userId))
            {
                await next(context);
            }
        }
        finally
        {
            AuditLogger.Ambient.Value = previous;
        }
    }
}

public static class HttpContextExtensions
{
    /// <summary>Client address after forwarded-header processing (when enabled for trusted proxies).</summary>
    public static string? GetClientIp(this HttpContext context) => context.Connection.RemoteIpAddress?.ToString();

    /// <summary>Name recorded as the technician on diagnostic steps, repairs, and notes (never client-supplied).</summary>
    public static string? TechnicianName(this ClaimsPrincipal user) => user.GetDisplayName();
}
