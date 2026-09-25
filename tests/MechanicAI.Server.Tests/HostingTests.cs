using System.Net;
using System.Net.Http.Json;
using MechanicAI.Application.Common;
using MechanicAI.Server.Contracts;
using MechanicAI.Server.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MechanicAI.Server.Tests;

public sealed class ResultMappingTests
{
    [Theory]
    [InlineData(ErrorKind.Validation, 400)]
    [InlineData(ErrorKind.NotFound, 404)]
    [InlineData(ErrorKind.Conflict, 409)]
    [InlineData(ErrorKind.Unauthorized, 401)]
    [InlineData(ErrorKind.RateLimited, 429)]
    [InlineData(ErrorKind.NotConfigured, 503)]
    [InlineData(ErrorKind.Unavailable, 503)]
    [InlineData(ErrorKind.Offline, 503)]
    [InlineData(ErrorKind.Timeout, 504)]
    [InlineData(ErrorKind.Cancelled, 499)]
    [InlineData(ErrorKind.InvalidResponse, 502)]
    [InlineData(ErrorKind.Unexpected, 500)]
    public void Error_kinds_map_to_status_codes(ErrorKind kind, int status)
    {
        var problem = new Error(kind, "message").ToProblem();
        Assert.Equal(status, problem.StatusCode);
        Assert.Equal(kind.ToString(), problem.ProblemDetails.Extensions["errorKind"]);
    }

    [Fact]
    public void Unexpected_errors_never_leak_details()
    {
        var error = Error.FromException(new InvalidOperationException("secret connection string Password=hunter2"));
        var problem = error.ToProblem();

        Assert.Equal(500, problem.StatusCode);
        Assert.DoesNotContain("hunter2", problem.ProblemDetails.Detail);
        Assert.DoesNotContain("hunter2", problem.ProblemDetails.Title);
        Assert.DoesNotContain(problem.ProblemDetails.Extensions.Values, v => v?.ToString()?.Contains("hunter2", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Success_results_map_to_ok_or_no_content()
    {
        Assert.IsType<NoContent>(Result.Success().ToHttp());
        Assert.IsType<Ok<int>>(Result.Success(42).ToHttp());
    }
}

public sealed class StartupTests
{
    private sealed class ProductionWithoutKey : ServerFactory
    {
        protected override string EnvironmentName => "Production";

        protected override IReadOnlyDictionary<string, string?> Overrides => new Dictionary<string, string?> { ["Jwt:SigningKey"] = "too-short" };
    }

    private sealed class DevelopmentWithoutKey : ServerFactory
    {
        protected override string EnvironmentName => "Development";

        protected override IReadOnlyDictionary<string, string?> Overrides => new Dictionary<string, string?> { ["Jwt:SigningKey"] = string.Empty };
    }

    [Fact]
    public void Production_refuses_to_start_without_a_strong_signing_key()
    {
        using var factory = new ProductionWithoutKey();
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Jwt:SigningKey", ex.ToString());
    }

    [Fact]
    public async Task Development_uses_an_ephemeral_key_and_serves_openapi()
    {
        await using var factory = new DevelopmentWithoutKey();
        await factory.LoginAsync();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }
}

public sealed class RateLimitTests
{
    private sealed class StrictAuthLimits : ServerFactory
    {
        protected override IReadOnlyDictionary<string, string?> Overrides => new Dictionary<string, string?>
        {
            ["RateLimiting:Auth:PermitLimit"] = "3",
            ["RateLimiting:Auth:WindowSeconds"] = "600",
        };
    }

    [Fact]
    public async Task Auth_endpoints_are_rate_limited()
    {
        await using var factory = new StrictAuthLimits();
        using var client = factory.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("nobody@shop.test", "wrong-password-1"), ServerFactory.Json);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests],
            statuses);

        // Health checks are never limited.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }
}
