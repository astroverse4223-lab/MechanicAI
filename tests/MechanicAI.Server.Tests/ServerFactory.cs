using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MechanicAI.Application.Abstractions;
using MechanicAI.Infrastructure.Security;
using MechanicAI.Server.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MechanicAI.Server.Tests;

/// <summary>
/// Runs the real server pipeline against a temporary SQLite database (Database:Provider=Sqlite),
/// with a fast password hasher and a bootstrapped owner account.
/// </summary>
public class ServerFactory : WebApplicationFactory<Program>
{
    public const string OwnerEmail = "owner@shop.test";
    public const string OwnerPassword = "Owner-Passw0rd!";
    public const int TestHashIterations = 10_000;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mechanicai-server-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Extra settings layered over the defaults (e.g. tighter limits for a specific test).</summary>
    protected virtual IReadOnlyDictionary<string, string?> Overrides => new Dictionary<string, string?>();

    protected virtual string EnvironmentName => "Testing";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_root);
        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["ConnectionStrings:MechanicAI"] = $"Data Source={Path.Combine(_root, "server.db")};Pooling=False;Default Timeout=30",
            ["Storage:DataRoot"] = _root,
            ["Jwt:SigningKey"] = "integration-tests-signing-key-0123456789-abcdefghijklmnop",
            ["Jwt:AccessTokenMinutes"] = "15",
            ["Bootstrap:AdminEmail"] = OwnerEmail,
            ["Bootstrap:AdminPassword"] = OwnerPassword,
            ["Bootstrap:AdminDisplayName"] = "Test Owner",
            ["RateLimiting:Auth:PermitLimit"] = "1000",
            ["RateLimiting:Api:PermitLimit"] = "10000",
            ["Uploads:MaxDocumentBytes"] = (1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["App:Ai:Mode"] = "Disabled",
            ["App:VehicleData:AutoCheckRecalls"] = "false",
            ["Serilog:MinimumLevel:Default"] = "Warning",
        };
        foreach (var (key, value) in Overrides) settings[key] = value;

        builder.UseEnvironment(EnvironmentName);
        foreach (var (key, value) in settings) builder.UseSetting(key, value);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPasswordHasher>();
            services.AddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher(TestHashIterations));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public async Task<TokenResponse> LoginAsync(string email = OwnerEmail, string password = OwnerPassword)
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password), Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
    }

    public async Task<HttpClient> CreateAuthenticatedClientAsync(string email = OwnerEmail, string password = OwnerPassword)
    {
        var tokens = await LoginAsync(email, password);
        return CreateClient(tokens.AccessToken);
    }

    public HttpClient CreateClient(string accessToken)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }
}

/// <summary>One server per test class (xunit class fixture).</summary>
public sealed class SharedServer : ServerFactory;
