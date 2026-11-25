using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using AvantraAgent.Knowledge;
using Avantra.AIAdvisory.Infrastructure.Persistence;
using Avantra.AIAdvisory.KnowledgeBase.Indexing;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AvantraAgent.Hosting;

public sealed class SecurityOptions
{
    public const string Section = "Security";
    /// <summary>
    /// Optional shared key for knowledge-management endpoints (create/delete knowledge bases, upload, sync, publish).
    /// Access control is normally done by the platform in front of AvantraAgent; set this as a second lock.
    /// Callers send it in the X-Admin-Key header.
    /// </summary>
    public string? AdminApiKey { get; set; }
    public string[] AllowedOrigins { get; set; } = [];
}

public sealed class RateLimitOptions
{
    public const string Section = "RateLimits";
    public int ChatPerMinute { get; set; } = 60;
    public int AdminPerMinute { get; set; } = 30;
}

/// <summary>Requires X-Admin-Key on knowledge-management endpoints when Security:AdminApiKey is set.</summary>
public sealed class AdminKeyFilter(IOptions<SecurityOptions> options) : IEndpointFilter
{
    public const string Header = "X-Admin-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = options.Value.AdminApiKey;
        if (!string.IsNullOrEmpty(expected))
        {
            var provided = context.HttpContext.Request.Headers[Header].ToString();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected)))
                return Results.Problem(title: "Admin key required", detail: $"Send the {Header} header.", statusCode: StatusCodes.Status401Unauthorized);
        }
        return await next(context);
    }
}

public sealed class KnowledgeReadinessCheck(KnowledgeReadiness readiness) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(readiness.IsReady ? HealthCheckResult.Healthy("Knowledge indexes loaded") : HealthCheckResult.Unhealthy("Loading knowledge indexes"));
}

public static class ProductionSetup
{
    public static IServiceCollection AddProductionHosting(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SecurityOptions>(config.GetSection(SecurityOptions.Section));
        services.Configure<RateLimitOptions>(config.GetSection(RateLimitOptions.Section));
        services.AddSingleton<AdminKeyFilter>();

        // Behind a gateway / load balancer: trust forwarded client IP and scheme.
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
        });

        var origins = config.GetSection($"{SecurityOptions.Section}:AllowedOrigins").Get<string[]>() ?? [];
        services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

        var limits = config.GetSection(RateLimitOptions.Section).Get<RateLimitOptions>() ?? new RateLimitOptions();
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy("chat", ctx => RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, limits.ChatPerMinute), Window = TimeSpan.FromMinutes(1) }));
            o.AddPolicy("admin", ctx => RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, limits.AdminPerMinute), Window = TimeSpan.FromMinutes(1) }));
        });

        services.AddHealthChecks()
            .AddDbContextCheck<AdvisoryDbContext>("database", tags: ["ready"])
            .AddCheck<KnowledgeReadinessCheck>("knowledge", tags: ["ready"]);

        // Fail fast at startup on misconfiguration.
        services.AddOptions<ConnectorIndexOptions>().Bind(config.GetSection(ConnectorIndexOptions.Section))
            .Validate(o => o.Provider is "InMemory" or "AzureAiSearch", "Search:Provider must be InMemory or AzureAiSearch.")
            .Validate(o => o.Provider != "AzureAiSearch" || Uri.TryCreate(o.Endpoint, UriKind.Absolute, out _), "Search:Endpoint is required for AzureAiSearch.")
            .ValidateOnStart();
        services.AddOptions<IngestionOptions>().Bind(config.GetSection(IngestionOptions.Section))
            .Validate(o => o.MaxFileSizeMb is > 0 and <= 200, "Ingestion:MaxFileSizeMb must be 1-200.")
            .Validate(o => o.MaxFilesPerUpload is > 0 and <= 100, "Ingestion:MaxFilesPerUpload must be 1-100.")
            .ValidateOnStart();
        services.AddOptions<Avantra.AIAdvisory.Infrastructure.AI.AiProviderOptions>()
            .Validate(o => string.IsNullOrWhiteSpace(o.Endpoint) || (!string.IsNullOrWhiteSpace(o.ApiKey) && !string.IsNullOrWhiteSpace(o.ChatModel)),
                "AI:Endpoint is set, so AI:ApiKey and AI:ChatModel are required too.")
            .ValidateOnStart();
        return services;
    }

    private static string ClientKey(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
