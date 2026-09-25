using System.Security.Claims;
using MechanicAI.Server.Auth;
using MechanicAI.Server.Contracts;
using MechanicAI.Server.Http;

namespace MechanicAI.Server.Endpoints;

public static class AuthEndpoints
{
    public const string AuthRateLimitPolicy = "auth";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");

        auth.MapPost("/login", async (LoginRequest request, AuthService service, HttpContext http, CancellationToken ct) =>
                (await service.LoginAsync(request.Email, request.Password, http.GetClientIp(), ct)).ToHttp())
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimitPolicy)
            .WithSummary("Sign in with email and password; returns an access token and a rotating refresh token.");

        auth.MapPost("/refresh", async (RefreshRequest request, AuthService service, HttpContext http, CancellationToken ct) =>
                (await service.RefreshAsync(request.RefreshToken, http.GetClientIp(), ct)).ToHttp())
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimitPolicy)
            .WithSummary("Exchange a refresh token for a new token pair. Each refresh token is single-use; reuse revokes its whole family.");

        auth.MapPost("/logout", async (LogoutRequest? request, AuthService service, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var authenticated = user.Identity?.IsAuthenticated == true;
                var tokenId = authenticated ? user.FindFirstValue(MechanicAiClaims.TokenId) : null;
                DateTime? expires = authenticated && long.TryParse(user.FindFirstValue("exp"), out var exp)
                    ? DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime
                    : null;
                await service.LogoutAsync(request?.RefreshToken, authenticated ? user.GetUserId() : null, tokenId, expires, ct);
                return TypedResults.NoContent();
            })
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimitPolicy)
            .WithSummary("Revoke the given refresh token and, when called with a bearer token, that access token too.");

        auth.MapGet("/me", async (ClaimsPrincipal user, AuthService service, CancellationToken ct) =>
                user.GetUserId() is { } id && await service.GetUserAsync(id, ct) is { } found
                    ? Results.Ok(UserDto.From(found))
                    : ResultMapping.NotFound("User"))
            .WithSummary("The signed-in user.");

        auth.MapPost("/change-password", async (ChangePasswordRequest request, ClaimsPrincipal user, AuthService service, CancellationToken ct) =>
                (await service.ChangePasswordAsync(user.GetUserId()!.Value, request.CurrentPassword, request.NewPassword, ct)).ToHttp())
            .RequireRateLimiting(AuthRateLimitPolicy)
            .WithSummary("Change your password. Signs out every other session.");

        var users = app.MapGroup("/api/users").WithTags("Users").RequireAuthorization(Policies.Admin);

        users.MapGet("/", async (AuthService service, CancellationToken ct) =>
            TypedResults.Ok((await service.ListUsersAsync(ct)).Select(UserDto.From).ToList()));

        users.MapPost("/", async (CreateUserRequest request, AuthService service, CancellationToken ct) =>
            (await service.CreateUserAsync(request, ct)).ToHttp(u => TypedResults.Created($"/api/users/{u.Id}", UserDto.From(u))));

        users.MapPut("/{id:guid}", async (Guid id, UpdateUserRequest request, ClaimsPrincipal user, AuthService service, CancellationToken ct) =>
            (await service.UpdateUserAsync(id, request, user.GetUserId()!.Value, ct)).ToHttp(u => TypedResults.Ok(UserDto.From(u))));

        users.MapPost("/{id:guid}/reset-password", async (Guid id, ResetPasswordRequest request, AuthService service, CancellationToken ct) =>
            (await service.ResetPasswordAsync(id, request.NewPassword, ct)).ToHttp());

        return app;
    }
}
