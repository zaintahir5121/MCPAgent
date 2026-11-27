using System.Text.Json;
using System.Text.Json.Serialization;
using AvantraAgent.Advisory;
using AvantraAgent.Chat;
using AvantraAgent.Hosting;
using AvantraAgent.Knowledge;
using AvantraAgent.Mcp;
using Avantra.AIAdvisory.Agents;
using Avantra.AIAdvisory.Application;
using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Application.Knowledge;
using Avantra.AIAdvisory.Domain.Catalogue;
using Avantra.AIAdvisory.Domain.Knowledge;
using Avantra.AIAdvisory.Infrastructure;
using Avantra.AIAdvisory.Infrastructure.Persistence;
using Avantra.AIAdvisory.KnowledgeBase;
using Avantra.AIAdvisory.KnowledgeBase.Indexing;
using Avantra.AIAdvisory.KnowledgeBase.Ingestion;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

// AvantraAgent: chat page (/) + REST API (/api) + MCP server (/mcp) in one service.
// Knowledge bases are data: add as many as you need at runtime, each with its own search index.
// Access management is done by the platform in front of the service (optional X-Admin-Key as a second lock).
var builder = WebApplication.CreateBuilder(args);

// ---- Configuration sources: appsettings → environment variables → Key Vault (managed identity)
if (builder.Configuration["KeyVault:Uri"] is { Length: > 0 } kv)
    builder.Configuration.AddAzureKeyVault(new Uri(kv), new Azure.Identity.DefaultAzureCredential());

// ---- Observability: OpenTelemetry → Application Insights when configured
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    builder.Services.AddOpenTelemetry().UseAzureMonitor()
        .WithTracing(t => t.AddSource("Avantra.AIAdvisory.AI", "Experimental.Microsoft.Extensions.AI", "*Microsoft.Agents.AI"));

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();
builder.Services.AddProductionHosting(builder.Configuration);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 210L * 1024 * 1024);

// ---- Shared engine, storage and AI provider
builder.Services.AddScoped<IUserContext, UpstreamUserContext>();
builder.Services.AddAdvisoryApplication()
    .AddAdvisoryInfrastructure(builder.Configuration)
    .AddKnowledgeCore(builder.Configuration)
    .AddAdvisoryAgents();

// ---- Knowledge bases (runtime registry) and one index per knowledge base
builder.Services.AddSingleton<EfKnowledgeBaseRegistry>();
builder.Services.AddSingleton<IKnowledgeBaseRegistry>(sp => sp.GetRequiredService<EfKnowledgeBaseRegistry>());
builder.Services.AddSingleton<ConnectorIndexRouter>();
builder.Services.AddSingleton<IKnowledgeIndex>(sp => sp.GetRequiredService<ConnectorIndexRouter>());
builder.Services.Configure<KnowledgeBaseSeedOptions>(builder.Configuration.GetSection(KnowledgeBaseSeedOptions.Section));
builder.Services.AddSingleton<KnowledgeReadiness>();
builder.Services.AddSingleton<IngestionQueue>();
builder.Services.AddHostedService<IngestionWorker>();
builder.Services.AddHostedService<CrawlWorker>();
builder.Services.AddSingleton<WebsiteCrawler>();
builder.Services.AddHttpClient("crawler", c => c.Timeout = TimeSpan.FromSeconds(60))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All });
builder.Services.AddHostedService<KnowledgeSyncScheduler>();
builder.Services.AddScoped<KnowledgeBaseService>();
builder.Services.AddHttpClient("ingestion", c => c.Timeout = TimeSpan.FromSeconds(60))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

// ---- Advisory tools, Planview live API, chat agent, MCP server
builder.Services.AddScoped<AdvisoryTools>();
builder.Services.AddSingleton<ChatSessionStore>();
builder.Services.AddScoped<ChatService>();
var mcpJson = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
mcpJson.Converters.Add(new JsonStringEnumConverter());
builder.Services.AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "avantra-agent", Version = "1.0.0", Title = "AvantraAgent" };
        o.ServerInstructions = "AvantraAgent: technology advisory over multiple knowledge bases (Planview, and any others added). " +
                               "Use knowledge_list to see them, ask_advisor for plain-language questions, or the knowledge_*/advisory_* tools directly. " +
                               "Show sources. Never state prices or licence tiers the tools did not return.";
    })
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<AgentMcpTools>(mcpJson);

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler(e => e.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = ex switch
    {
        KeyNotFoundException or UnknownVendorException => (404, "Not found"),
        InvalidOperationException => (409, "Conflict"),
        QueueFullException => (503, "Busy"),
        NotSupportedException => (415, "Unsupported document type"),
        ArgumentException or BadHttpRequestException => (400, "Invalid request"),
        _ => (500, "Unexpected error"),
    };
    ctx.Response.StatusCode = status;
    await Results.Problem(title: title, detail: status < 500 || status == 503 ? ex?.Message : "See the service logs for the trace id.", statusCode: status,
        extensions: new Dictionary<string, object?> { ["traceId"] = System.Diagnostics.Activity.Current?.Id ?? ctx.TraceIdentifier }).ExecuteAsync(ctx);
}));
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseCors();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapOpenApi();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

var api = app.MapGroup("/api");

api.MapGet("/status", async (IAiProviderInfo ai, KnowledgeBaseService kbs, KnowledgeReadiness ready, IOptions<SecurityOptions> sec, CancellationToken ct) => new
{
    service = "AvantraAgent",
    version = typeof(Program).Assembly.GetName().Version?.ToString(),
    mode = ai.ChatEnabled ? "agent (AI)" : "deterministic (no AI key)",
    model = ai.ChatModel,
    ready = ready.IsReady,
    adminKeyRequired = !string.IsNullOrEmpty(sec.Value.AdminApiKey),
    knowledgeBases = await kbs.ListAsync(ct),
}).WithTags("Status");

api.MapGet("/discovery/{chatId}", (string chatId, ChatService chat) => chat.GetDiscovery(chatId) is { } d ? Results.Ok(d) : Results.NotFound())
   .WithTags("Discovery").WithSummary("Current discovery assessment of a conversation.");
api.MapGet("/discovery/{chatId}/summary", (string chatId, ChatService chat) => chat.GetDiscovery(chatId) is { } d
        ? Results.File(System.Text.Encoding.UTF8.GetBytes(DiscoveryBuilder.ToMarkdown(d)), "text/markdown", $"discovery-{chatId[..Math.Min(8, chatId.Length)]}.md")
        : Results.NotFound())
   .WithTags("Discovery").WithSummary("Discovery summary (Markdown) to share by email, CRM or in the solution workshop.");

api.MapPost("/chat", (ChatRequest r, ChatService chat, CancellationToken ct) => chat.ReplyAsync(r, ct))
   .RequireRateLimiting("chat").WithTags("Chat").WithSummary("Ask AvantraAgent (optionally focused on one knowledge base).");

// ---- Knowledge bases: read
var kb = api.MapGroup("/knowledge-bases").WithTags("Knowledge bases");
kb.MapGet("/", (KnowledgeBaseService s, CancellationToken ct) => s.ListAsync(ct));
kb.MapGet("/{code}", (string code, KnowledgeBaseService s, CancellationToken ct) => s.GetAsync(code, ct));
kb.MapGet("/{code}/search", (string code, string q, int? top, AdvisoryTools t, CancellationToken ct) => t.SearchAsync(code, q, top ?? 5, ct));
kb.MapGet("/{code}/documents", async (string code, KnowledgeBaseService s, IKnowledgeStore store, CancellationToken ct) =>
{
    s.Require(code);
    return (await store.ListAsync(code, null, ct)).Select(d => new { d.Id, d.Title, d.ExternalId, d.Version, d.Status, d.StatusReason, d.Trust, d.SourceUrl, d.IngestedAt, d.IngestedBy });
});
kb.MapGet("/{code}/products", (string code, AdvisoryTools t) => t.ListProducts(code));
kb.MapGet("/{code}/jobs", (string code, int? take, KnowledgeBaseService s, CancellationToken ct) => s.JobsAsync(code, take ?? 20, ct));
api.MapGet("/jobs/{id:guid}", async (Guid id, KnowledgeBaseService s, CancellationToken ct) => await s.JobAsync(id, ct) is { } j ? Results.Ok(j) : Results.NotFound()).WithTags("Knowledge bases");

// ---- Knowledge bases: manage (optional admin key + rate limit)
var admin = api.MapGroup("/knowledge-bases").WithTags("Knowledge management").AddEndpointFilter<AdminKeyFilter>().RequireRateLimiting("admin");
admin.MapPost("/", async (CreateKnowledgeBaseRequest r, KnowledgeBaseService s, CancellationToken ct) =>
        Results.Created($"/api/knowledge-bases/{r.Code?.ToLowerInvariant()}", await s.CreateAsync(r, ct)))
     .WithSummary("Create a knowledge base (gets its own search index).");
admin.MapPatch("/{code}", (string code, UpdateKnowledgeBaseRequest r, KnowledgeBaseService s, CancellationToken ct) => s.UpdateAsync(code, r, ct));
admin.MapDelete("/{code}", async (string code, KnowledgeBaseService s, CancellationToken ct) => { await s.DeleteAsync(code, ct); return Results.NoContent(); })
     .WithSummary("Delete a knowledge base and its documents.");
admin.MapPost("/{code}/documents", async (string code, [FromForm] IFormFileCollection files, [FromForm] bool? publish, [FromForm] SourceTrustLevel? trust,
        [FromForm] string? sourceUrl, KnowledgeBaseService s, CancellationToken ct) =>
    {
        var job = await s.UploadAsync(code, files, publish, trust, sourceUrl, ct);
        return Results.Accepted($"/api/jobs/{job.Id}", JobView.From(job, details: false));
    })
     .DisableAntiforgery()
     .WithSummary("Upload one or more documents (PDF, DOCX, HTML, MD, TXT). Processed in the background; poll the returned job.");
admin.MapPost("/{code}/documents/from-url", async (string code, UrlIngestionRequest r, KnowledgeBaseService s, CancellationToken ct) =>
    {
        var job = await s.IngestUrlAsync(code, r, ct);
        return Results.Accepted($"/api/jobs/{job.Id}", JobView.From(job, details: false));
    })
     .WithSummary("Download a document from an allowed https host and ingest it.");
admin.MapPost("/{code}/sync", async (string code, KnowledgeBaseService s, CancellationToken ct) =>
    {
        var job = await s.SyncAsync(code, ct);
        return Results.Accepted($"/api/jobs/{job.Id}", JobView.From(job, details: false));
    })
     .WithSummary("Ingest the knowledge base's source folder now (unchanged files are skipped).");
admin.MapGet("/{code}/crawl-config", (string code, KnowledgeBaseService s) => s.GetCrawlConfig(code) is { } c ? Results.Ok(c) : Results.NoContent());
admin.MapPut("/{code}/crawl-config", (string code, CrawlConfig? cfg, KnowledgeBaseService s, CancellationToken ct) => s.SetCrawlConfigAsync(code, cfg, ct))
     .WithSummary("Set the website crawl (sitemaps, include/exclude paths, max pages, delay).");
admin.MapPost("/{code}/crawl", async (string code, StartCrawlRequest? r, KnowledgeBaseService s, CancellationToken ct) =>
    {
        var job = await s.StartCrawlAsync(code, r?.MaxPages, ct, r?.Force ?? false);
        return Results.Accepted($"/api/jobs/{job.Id}", JobView.From(job, details: false));
    })
     .WithSummary("Crawl the configured website now (polite: robots.txt and crawl delays; unchanged pages are skipped).");
api.MapPost("/jobs/{id:guid}/cancel", async (Guid id, KnowledgeBaseService s, CancellationToken ct) =>
        await s.CancelJobAsync(id, ct) ? Results.Accepted($"/api/jobs/{id}") : Results.NotFound())
   .WithTags("Knowledge management").AddEndpointFilter<AdminKeyFilter>();
admin.MapPost("/{code}/documents/{id:guid}/publish", async (string code, Guid id, KnowledgeBaseService s, IngestionService ingestion, CancellationToken ct) =>
    {
        s.Require(code);
        return await ingestion.PublishAsync(id, ct) is { } d && d.VendorCode == code ? Results.Ok(new { d.Id, d.Title, d.Status }) : Results.NotFound();
    });
admin.MapDelete("/{code}/documents/{id:guid}", async (string code, Guid id, KnowledgeBaseService s, CancellationToken ct) =>
    await s.DeleteDocumentAsync(code, id, ct) ? Results.NoContent() : Results.NotFound());

app.MapMcp("/mcp").RequireRateLimiting("chat");

// ---- Startup: database schema, knowledge base registry, seed knowledge bases from configuration
await app.Services.InitializeDatabaseAsync();
var registry = app.Services.GetRequiredService<EfKnowledgeBaseRegistry>();
await registry.LoadAsync(CancellationToken.None);
foreach (var seed in app.Services.GetRequiredService<IOptions<KnowledgeBaseSeedOptions>>().Value.Seed)
{
    if (registry.Get(seed.Code) is null)
        await registry.CreateAsync(seed.ToDefinition(), CancellationToken.None);
    else if (seed.Crawl is not null && string.IsNullOrWhiteSpace(registry.Get(seed.Code)!.CrawlConfigJson))
        await registry.UpdateAsync(seed.Code, k => k.CrawlConfigJson = seed.ToDefinition().CrawlConfigJson, CancellationToken.None);
}
if (!app.Environment.IsDevelopment() && string.IsNullOrEmpty(app.Configuration["Security:AdminApiKey"]))
    app.Logger.LogWarning("Security:AdminApiKey is not set: knowledge-management endpoints rely entirely on the platform in front of AvantraAgent.");

await app.RunAsync();

public partial class Program;
