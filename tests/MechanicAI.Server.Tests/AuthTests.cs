using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MechanicAI.Application.Abstractions;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using MechanicAI.Infrastructure.Security;
using MechanicAI.Server.Auth;
using MechanicAI.Server.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MechanicAI.Server.Tests;

public sealed class AuthTests(SharedServer server) : IClassFixture<SharedServer>
{
    private static readonly JsonSerializerOptions Json = ServerFactory.Json;

    [Fact]
    public async Task Login_with_valid_credentials_returns_token_pair_and_me_works()
    {
        var tokens = await server.LoginAsync();

        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(tokens.RefreshToken));
        Assert.Equal("Bearer", tokens.TokenType);
        Assert.Equal(UserRole.Owner, tokens.User.Role);
        Assert.True(tokens.RefreshTokenExpiresUtc > tokens.AccessTokenExpiresUtc);

        using var client = server.CreateClient(tokens.AccessToken);
        var me = await client.GetFromJsonAsync<UserDto>("/api/auth/me", Json);
        Assert.Equal(ServerFactory.OwnerEmail, me!.Email);
    }

    [Fact]
    public async Task Refresh_tokens_are_stored_only_as_hashes()
    {
        var tokens = await server.LoginAsync();
        var hash = TokenService.HashRefreshToken(tokens.RefreshToken);

        await using var db = await server.Services.GetRequiredService<IAppDbContextFactory>().CreateAsync();
        Assert.True(await db.RefreshTokens.AnyAsync(t => t.TokenHash == hash));
        Assert.False(await db.RefreshTokens.AnyAsync(t => t.TokenHash == tokens.RefreshToken));
    }

    [Theory]
    [InlineData(ServerFactory.OwnerEmail, "wrong-password-123")]
    [InlineData("nobody@shop.test", "whatever-password-1")]
    [InlineData("", "")]
    public async Task Login_with_bad_credentials_returns_401_problem(string email, string password)
    {
        using var client = server.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password), Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid email or password.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account()
    {
        using var admin = await server.CreateAuthenticatedClientAsync();
        var email = $"lock-{Guid.NewGuid():N}@shop.test";
        const string password = "Locked-Passw0rd!";
        (await admin.PostAsJsonAsync("/api/users", new CreateUserRequest(email, "Lock Test", password, UserRole.Technician), Json)).EnsureSuccessStatusCode();

        using var client = server.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            var failed = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "not-the-password-1"), Json);
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        // Even the right password is refused while locked out.
        var locked = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password), Json);
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_the_refresh_token()
    {
        var first = await server.LoginAsync();
        using var client = server.CreateClient();

        var second = await RefreshAsync(client, first.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondTokens = (await second.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
        Assert.NotEqual(first.RefreshToken, secondTokens.RefreshToken);

        var third = await RefreshAsync(client, secondTokens.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);

        using var authed = server.CreateClient(secondTokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await authed.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Reusing_a_rotated_refresh_token_revokes_the_whole_family()
    {
        var first = await server.LoginAsync();
        using var client = server.CreateClient();

        var rotated = (await (await RefreshAsync(client, first.RefreshToken)).Content.ReadFromJsonAsync<TokenResponse>(Json))!;

        // An attacker replays the old token...
        var replay = await RefreshAsync(client, first.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // ...which also kills the legitimate successor.
        var successor = await RefreshAsync(client, rotated.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, successor.StatusCode);

        // Other sessions of the same user are unaffected.
        var other = await server.LoginAsync();
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, other.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Unknown_refresh_token_returns_401()
    {
        using var client = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, "not-a-real-token")).StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_refresh_and_access_tokens()
    {
        var tokens = await server.LoginAsync();
        using var client = server.CreateClient(tokens.AccessToken);

        var logout = await client.PostAsJsonAsync("/api/auth/logout", new LogoutRequest(tokens.RefreshToken), Json);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        using var anonymous = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(anonymous, tokens.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Protected_endpoints_require_a_token()
    {
        using var client = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/vehicles")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);

        using var forged = server.CreateClient("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync("/api/vehicles")).StatusCode);
    }

    [Fact]
    public async Task Login_upgrades_an_outdated_password_hash()
    {
        var email = $"legacy-{Guid.NewGuid():N}@shop.test";
        const string password = "Legacy-Passw0rd!";
        var factory = server.Services.GetRequiredService<IAppDbContextFactory>();
        await using (var db = await factory.CreateAsync())
        {
            db.Users.Add(new User
            {
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                DisplayName = "Legacy",
                PasswordHash = new Pbkdf2PasswordHasher(1_000).Hash(password),
                Role = UserRole.Technician,
            });
            await db.SaveChangesAsync();
        }

        await server.LoginAsync(email, password);

        await using var check = await factory.CreateAsync();
        var stored = await check.Users.AsNoTracking().SingleAsync(u => u.Email == email);
        Assert.StartsWith($"PBKDF2$SHA256${ServerFactory.TestHashIterations}$", stored.PasswordHash);
    }

    [Fact]
    public async Task Changing_password_invalidates_existing_tokens()
    {
        using var admin = await server.CreateAuthenticatedClientAsync();
        var email = $"change-{Guid.NewGuid():N}@shop.test";
        const string password = "Original-Passw0rd!";
        (await admin.PostAsJsonAsync("/api/users", new CreateUserRequest(email, "Change Test", password, UserRole.Technician), Json)).EnsureSuccessStatusCode();

        var tokens = await server.LoginAsync(email, password);
        using var client = server.CreateClient(tokens.AccessToken);
        var change = await client.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest(password, "Replacement-Passw0rd!"), Json);
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        using var anonymous = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(anonymous, tokens.RefreshToken)).StatusCode);
        await server.LoginAsync(email, "Replacement-Passw0rd!");
    }

    [Fact]
    public async Task Role_policies_restrict_writes()
    {
        using var admin = await server.CreateAuthenticatedClientAsync();
        var email = $"apprentice-{Guid.NewGuid():N}@shop.test";
        const string password = "Apprentice-Passw0rd!";
        (await admin.PostAsJsonAsync("/api/users", new CreateUserRequest(email, "Apprentice", password, UserRole.Apprentice), Json)).EnsureSuccessStatusCode();

        using var apprentice = await server.CreateAuthenticatedClientAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, (await apprentice.GetAsync("/api/vehicles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await apprentice.GetAsync("/api/users")).StatusCode);
        var customer = await apprentice.PostAsJsonAsync("/api/customers", new CustomerRequest("A", "B", null, null, null, null, null, null, null, null), Json);
        Assert.Equal(HttpStatusCode.Forbidden, customer.StatusCode);
    }

    [Fact]
    public async Task Creating_a_duplicate_user_returns_409()
    {
        using var admin = await server.CreateAuthenticatedClientAsync();
        var response = await admin.PostAsJsonAsync("/api/users",
            new CreateUserRequest(ServerFactory.OwnerEmail.ToUpperInvariant(), "Dup", "Another-Passw0rd!", UserRole.Technician), Json);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Weak_passwords_are_rejected()
    {
        using var admin = await server.CreateAuthenticatedClientAsync();
        var response = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest("weak@shop.test", "Weak", "short", UserRole.Technician), Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken) =>
        client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(refreshToken), Json);
}
