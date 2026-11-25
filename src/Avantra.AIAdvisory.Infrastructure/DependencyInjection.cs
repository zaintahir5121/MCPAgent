using System.ClientModel;
using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Infrastructure.AI;
using Avantra.AIAdvisory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;

namespace Avantra.AIAdvisory.Infrastructure;

public sealed class AiProviderInfo(IOptions<AiProviderOptions> options) : IAiProviderInfo
{
    public bool ChatEnabled => options.Value.ChatConfigured;
    public bool EmbeddingsEnabled => options.Value.EmbeddingsConfigured;
    public string? ChatModel => options.Value.ChatModel;
}

public static class DependencyInjection
{
    public static IServiceCollection AddAdvisoryInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? "Sqlite";
        var cs = configuration.GetConnectionString("Advisory") ?? "Data Source=advisory.db";
        services.AddDbContext<AdvisoryDbContext>(o =>
        {
            if (string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
                o.UseSqlServer(cs, sql => sql.EnableRetryOnFailure());
            else
                o.UseSqlite(cs);
        });

        services.AddScoped<IKnowledgeStore, EfKnowledgeStore>();
        services.AddScoped<ISessionStore, EfSessionStore>();
        services.AddScoped<IAuditLog, EfAuditLog>();

        services.AddAiProvider(configuration);
        return services;
    }

    /// <summary>
    /// Registers <see cref="IChatClient"/> / <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> (Microsoft.Extensions.AI)
    /// over any OpenAI-compatible endpoint. Everything downstream depends only on the abstractions, so the model
    /// or vendor can be swapped by configuration.
    /// </summary>
    public static IServiceCollection AddAiProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiProviderOptions>(configuration.GetSection(AiProviderOptions.Section));
        services.AddSingleton<IAiProviderInfo, AiProviderInfo>();

        var options = configuration.GetSection(AiProviderOptions.Section).Get<AiProviderOptions>() ?? new AiProviderOptions();
        if (!options.ChatConfigured) return services;

        services.AddSingleton(_ => new OpenAIClient(new ApiKeyCredential(options.ApiKey!), new OpenAIClientOptions
        {
            Endpoint = new Uri(options.Endpoint!),
            NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
            UserAgentApplicationId = "avantra-ai-advisory",
        }));

        services.AddChatClient(sp => sp.GetRequiredService<OpenAIClient>().GetChatClient(options.ChatModel!).AsIChatClient())
            .UseLogging()
            .UseOpenTelemetry(sourceName: "Avantra.AIAdvisory.AI", configure: c => c.EnableSensitiveData = false);

        if (options.EmbeddingsConfigured)
        {
            services.AddEmbeddingGenerator(sp => sp.GetRequiredService<OpenAIClient>().GetEmbeddingClient(options.EmbeddingModel!).AsIEmbeddingGenerator())
                .UseLogging()
                .UseOpenTelemetry(sourceName: "Avantra.AIAdvisory.AI");
        }
        return services;
    }

    public static async Task InitializeDatabaseAsync(this IServiceProvider sp, CancellationToken ct = default)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AdvisoryDbContext>();
        // SQL Server / Azure SQL (production): apply EF Core migrations (Persistence/Migrations).
        // SQLite (local development): create the schema directly; delete the .db file after schema changes.
        if (db.Database.IsSqlServer()) await db.Database.MigrateAsync(ct);
        else await db.Database.EnsureCreatedAsync(ct);
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Database")
            .LogInformation("Database ready ({Provider})", db.Database.ProviderName);
    }
}
