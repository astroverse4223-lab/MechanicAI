using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using MechanicAI.Application;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Services;
using MechanicAI.Infrastructure;
using MechanicAI.Infrastructure.Persistence;
using MechanicAI.Infrastructure.Platform;
using MechanicAI.Server.Auth;
using MechanicAI.Server.Endpoints;
using MechanicAI.Server.Http;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Serilog;

namespace MechanicAI.Server.Hosting;

public static class ServerSetup
{
    public const string CorsPolicy = "configured-origins";
    public const string ApiRateLimitPolicy = "api";

    /// <summary>Registers everything the shop server needs. Reads configuration eagerly where the choice changes the service graph.</summary>
    public static WebApplicationBuilder AddMechanicAiServer(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var configuration = builder.Configuration;

        services.AddSerilog((sp, logger) => logger
            .ReadFrom.Configuration(configuration)
            .ReadFrom.Services(sp)
            .Enrich.FromLogContext());

        // ------------------------------------------------------------ options
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.Section));
        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.Section));
        services.AddOptions<AuthOptions>().Bind(configuration.GetSection(AuthOptions.Section));
        services.AddOptions<BootstrapOptions>().Bind(configuration.GetSection(BootstrapOptions.Section));
        services.AddOptions<UploadOptions>().Bind(configuration.GetSection(UploadOptions.Section));
        ConfigureJwtOptions(builder);

        // ------------------------------------------------------------ application + infrastructure
        var database = configuration.GetSection(DatabaseOptions.Section).Get<DatabaseOptions>() ?? new DatabaseOptions();
        var storage = configuration.GetSection(StorageOptions.Section).Get<StorageOptions>() ?? new StorageOptions();
        var dataRoot = string.IsNullOrWhiteSpace(storage.DataRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "MechanicAI.Server")
            : storage.DataRoot;
        var connectionString = configuration.GetConnectionString("MechanicAI");
        if (database.Provider == DatabaseProvider.Postgres && string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:MechanicAI is required when Database:Provider is Postgres. Set it with the ConnectionStrings__MechanicAI environment variable.");
        }

        // Server replacements for workstation stores (registered first; infrastructure uses TryAdd).
        services.AddSingleton<ISettingsStore, ConfigurationSettingsStore>();
        services.AddSingleton<ISecretStore, ConfigurationSecretStore>();
        services.AddMechanicAiInfrastructure(o =>
        {
            o.Database = database.Provider;
            o.ConnectionString = string.IsNullOrWhiteSpace(connectionString) ? null : connectionString;
            o.DataRoot = dataRoot;
            o.Workstation = false;
        });
        services.AddMechanicAiApplication();
        services.TryAddSingleton<KnowledgeBaseService>();
        services.TryAddSingleton<TrainingService>();
        services.TryAddSingleton<ResearchService>();

        // Document indexing runs in the background (the workstation flag leaves this off).
        services.TryAddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
        services.AddHostedService<BackgroundTaskProcessor>();

        // ------------------------------------------------------------ caching (Redis optional)
        services.AddMemoryCache();
        var redis = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redis))
        {
            services.AddStackExchangeRedisCache(o =>
            {
                o.Configuration = redis;
                o.InstanceName = "mechanicai:";
            });
        }
        else
        {
            services.AddDistributedMemoryCache();
        }

        // ------------------------------------------------------------ authentication & authorization
        services.AddSingleton<TokenService>();
        services.AddSingleton<AccessTokenValidator>();
        services.AddSingleton<AuthService>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((o, options) =>
            {
                o.MapInboundClaims = false;
                o.RequireHttpsMetadata = false;
                o.TokenValidationParameters = TokenService.CreateValidationParameters(options.Value);
                o.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var validator = context.HttpContext.RequestServices.GetRequiredService<AccessTokenValidator>();
                        var failure = await validator.ValidateAsync(context.Principal!, context.HttpContext.RequestAborted);
                        if (failure is not null) context.Fail(failure);
                    },
                };
            });
        services.AddAuthorizationBuilder().AddMechanicAiPolicies();

        // ------------------------------------------------------------ HTTP pipeline services
        services.AddProblemDetails(o => o.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;
            context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        });
        services.AddExceptionHandler<ServerExceptionHandler>();
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddOpenApi(o => o.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "Mechanic AI shop server";
            document.Info.Description = "Authenticate with POST /api/auth/login and send the access token as 'Authorization: Bearer <token>'.";
            return Task.CompletedTask;
        }));

        var dbCheckTags = new[] { "ready" };
        if (database.Provider == DatabaseProvider.Postgres)
        {
            services.TryAddScoped(sp => sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<PostgresAppDbContext>>().CreateDbContext());
            services.AddHealthChecks().AddDbContextCheck<PostgresAppDbContext>("database", tags: dbCheckTags);
        }
        else
        {
            services.TryAddScoped(sp => sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<SqliteAppDbContext>>().CreateDbContext());
            services.AddHealthChecks().AddDbContextCheck<SqliteAppDbContext>("database", tags: dbCheckTags);
        }

        AddRateLimiting(services, configuration);

        var cors = configuration.GetSection(CorsSettings.Section).Get<CorsSettings>() ?? new CorsSettings();
        if (cors.AllowedOrigins.Count > 0)
        {
            services.AddCors(o => o.AddPolicy(CorsPolicy, p => p
                .WithOrigins([.. cors.AllowedOrigins])
                .AllowAnyHeader()
                .AllowAnyMethod()));
        }

        var forwarded = configuration.GetSection(ForwardedHeadersSettings.Section).Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
        if (forwarded.Enabled)
        {
            services.Configure<ForwardedHeadersOptions>(o =>
            {
                o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
                foreach (var proxy in forwarded.KnownProxies) o.KnownProxies.Add(IPAddress.Parse(proxy));
                foreach (var network in forwarded.KnownNetworks) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            });
        }

        var uploads = configuration.GetSection(UploadOptions.Section).Get<UploadOptions>() ?? new UploadOptions();
        services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = KnowledgeEndpoints.EffectiveUploadLimit(uploads) + KnowledgeEndpoints.MultipartOverheadBytes);

        return builder;
    }

    /// <summary>
    /// Validates the JWT signing key. Outside Development a missing or short key stops the server;
    /// in Development an ephemeral random key is generated (tokens do not survive a restart).
    /// </summary>
    private static JwtOptions ConfigureJwtOptions(WebApplicationBuilder builder)
    {
        var jwt = builder.Configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
        if (jwt.SigningKeyBytes.Length < JwtOptions.MinimumKeyBytes)
        {
            if (!builder.Environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    $"Jwt:SigningKey is missing or shorter than {JwtOptions.MinimumKeyBytes} bytes. Provide a random secret (e.g. `openssl rand -base64 48`) " +
                    "through the Jwt__SigningKey environment variable or user-secrets.");
            }

            jwt.SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            EphemeralSigningKey = true;
        }

        if (jwt.AccessTokenMinutes is < 1 or > 24 * 60) throw new InvalidOperationException("Jwt:AccessTokenMinutes must be between 1 and 1440.");
        if (jwt.RefreshTokenDays is < 1 or > 365) throw new InvalidOperationException("Jwt:RefreshTokenDays must be between 1 and 365.");

        builder.Services.AddOptions<JwtOptions>().Bind(builder.Configuration.GetSection(JwtOptions.Section))
            .PostConfigure(o => o.SigningKey = jwt.SigningKey);
        return jwt;
    }

    /// <summary>True when Development generated a throwaway signing key (logged as a warning at startup).</summary>
    public static bool EphemeralSigningKey { get; private set; }

    private static void AddRateLimiting(IServiceCollection services, IConfiguration configuration)
    {
        var limits = configuration.GetSection(RateLimitingOptions.Section).Get<RateLimitingOptions>() ?? new RateLimitingOptions();
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (context, ct) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                var problems = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
                await problems.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context.HttpContext,
                    ProblemDetails = new()
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = ResultMapping.TitleFor(StatusCodes.Status429TooManyRequests),
                        Detail = "Too many requests. Wait a moment and try again.",
                    },
                });
            };

            // Sign-in endpoints: per client IP, to slow password guessing.
            o.AddPolicy(AuthEndpoints.AuthRateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => Window(limits.Auth)));

            // Everything else: per user (or IP when anonymous).
            o.AddPolicy(ApiRateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.GetUserId()?.ToString() ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => Window(limits.Api)));
        });

        static FixedWindowRateLimiterOptions Window(RateLimitingOptions.WindowLimit limit) => new()
        {
            PermitLimit = Math.Max(1, limit.PermitLimit),
            Window = TimeSpan.FromSeconds(Math.Max(1, limit.WindowSeconds)),
            QueueLimit = 0,
            AutoReplenishment = true,
        };
    }

    /// <summary>Applies migrations and seeds reference content (when enabled), then bootstraps the first owner from configuration.</summary>
    public static async Task InitializeServerAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("MechanicAI.Server.Startup");
        if (EphemeralSigningKey)
        {
            logger.LogWarning("Jwt:SigningKey is not configured; using an ephemeral development key. Tokens will not survive a restart.");
        }

        var database = services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        if (database.ApplyMigrationsOnStartup)
        {
            await services.GetRequiredService<DatabaseInitializer>().InitializeAsync(ct);
            logger.LogInformation("Database ready ({Provider})", database.Provider);
        }

        var bootstrap = services.GetRequiredService<IOptions<BootstrapOptions>>().Value;
        if (!string.IsNullOrWhiteSpace(bootstrap.AdminEmail) && !string.IsNullOrEmpty(bootstrap.AdminPassword))
        {
            var auth = services.GetRequiredService<AuthService>();
            var created = await auth.BootstrapOwnerAsync(bootstrap.AdminEmail, bootstrap.AdminPassword, bootstrap.AdminDisplayName, ct);
            if (created.IsSuccess)
            {
                logger.LogWarning("Created the first owner account {Email} from Bootstrap configuration. Remove Bootstrap:AdminPassword from the environment now.",
                    created.Value!.Email);
            }
            else if (created.Error!.Kind != Application.Common.ErrorKind.Conflict)
            {
                throw new InvalidOperationException($"Bootstrap owner could not be created: {created.Error.Message}");
            }
        }
    }

    /// <summary>Configures the middleware pipeline and maps every endpoint group.</summary>
    public static WebApplication UseMechanicAiServer(this WebApplication app)
    {
        var configuration = app.Configuration;
        var forwarded = configuration.GetSection(ForwardedHeadersSettings.Section).Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
        if (forwarded.Enabled) app.UseForwardedHeaders();

        app.UseExceptionHandler();
        app.UseStatusCodePages();

        var https = configuration.GetSection(HttpsSettings.Section).Get<HttpsSettings>() ?? new HttpsSettings();
        if (https.Hsts && !app.Environment.IsDevelopment()) app.UseHsts();
        if (https.Redirect) app.UseHttpsRedirection();

        app.UseSerilogRequestLogging(o => o.GetLevel = (http, _, ex) =>
            ex is not null || http.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
            : http.Request.Path.StartsWithSegments("/health") ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information);

        var cors = configuration.GetSection(CorsSettings.Section).Get<CorsSettings>() ?? new CorsSettings();
        if (cors.AllowedOrigins.Count > 0) app.UseCors(CorsPolicy);

        app.UseAuthentication();
        app.UseMiddleware<AuditContextMiddleware>();
        app.UseRateLimiter();
        app.UseAuthorization();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi().AllowAnonymous();
            Scalar.AspNetCore.ScalarEndpointRouteBuilderExtensions.MapScalarApiReference(app).AllowAnonymous();
        }

        app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false })
            .AllowAnonymous()
            .DisableRateLimiting();
        app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") })
            .AllowAnonymous()
            .DisableRateLimiting();

        var api = app.MapGroup(string.Empty).RequireRateLimiting(ApiRateLimitPolicy);
        api.MapAuthEndpoints();
        api.MapVehicleEndpoints();
        api.MapCustomerEndpoints();
        api.MapSessionEndpoints();
        api.MapDtcEndpoints();
        api.MapKnowledgeEndpoints();
        api.MapShopEndpoints();
        api.MapTrainingEndpoints();
        api.MapResearchEndpoints();
        return app;
    }
}
