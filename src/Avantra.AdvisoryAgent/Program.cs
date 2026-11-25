using System.Text.Json;
using System.Text.Json.Serialization;
using Avantra.AdvisoryAgent.Chat;
using Avantra.AdvisoryAgent.Connectors.Planview;
using Avantra.AdvisoryAgent.Hosting;
using Avantra.AdvisoryAgent.Mcp;
using Avantra.AIAdvisory.Agents;
using Avantra.AIAdvisory.Application;
using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Application.Knowledge;
using Avantra.AIAdvisory.Domain.Catalogue;
using Avantra.AIAdvisory.Domain.Knowledge;
using Avantra.AIAdvisory.Infrastructure;
using Avantra.AIAdvisory.KnowledgeBase;
using Avantra.AIAdvisory.KnowledgeBase.Indexing;
using Avantra.AIAdvisory.KnowledgeBase.Ingestion;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ModelContextProtocol;

// ONE project: chat web page (/) + REST API (/api) + MCP server (/mcp).
// Access management is handled by the platform in front of this service (see UpstreamUserContext).
var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IUserContext, UpstreamUserContext>();

// Shared engine (requirements, recommendation, evidence, licensing, reports) + storage + AI provider
builder.Services.AddAdvisoryApplication()
    .AddAdvisoryInfrastructure(builder.Configuration)
    .AddKnowledgeCore(builder.Configuration)
    .AddAdvisoryAgents();

// One knowledge index per connector (planview-index now; salesforce-index / outsystems-index later)
builder.Services.Configure<ConnectorIndexOptions>(builder.Configuration.GetSection(ConnectorIndexOptions.Section));
builder.Services.AddSingleton<ConnectorIndexRouter>();
builder.Services.AddSingleton<IKnowledgeIndex>(sp => sp.GetRequiredService<ConnectorIndexRouter>());

// Planview connector
builder.Services.Configure<PlanviewConnectorOptions>(builder.Configuration.GetSection(PlanviewConnectorOptions.Section));
builder.Services.AddScoped<PlanviewConnector>();
builder.Services.AddHttpClient<PlanviewApiClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<PlanviewIngestionJob>();
builder.Services.AddHostedService<PlanviewIngestionScheduler>();

// Advisory agent (chat) + MCP server
builder.Services.AddSingleton<ChatSessionStore>();
builder.Services.AddScoped<ChatService>();
var mcpJson = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
mcpJson.Converters.Add(new JsonStringEnumConverter());
builder.Services.AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "avantra-advisory-agent", Version = "1.0.0", Title = "Avantra Advisory Agent" };
        o.ServerInstructions = "Avantra Advisory Agent. Use ask_advisor for plain-language questions, or the planview_* tools directly. " +
                               "Show sources. Never state prices or licence tiers the tools did not return.";
    })
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<PlanviewMcpTools>(mcpJson);

var app = builder.Build();

app.UseExceptionHandler(e => e.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = ex switch
    {
        IngestionBusyException => (409, "Ingestion already running"),
        UnknownVendorException => (404, "Unknown vendor"),
        ArgumentException => (400, "Invalid request"),
        NotSupportedException => (415, "Unsupported document type"),
        DirectoryNotFoundException => (500, "Source folder missing"),
        _ => (500, "Unexpected error"),
    };
    ctx.Response.StatusCode = status;
    await Results.Problem(title: title, detail: status < 500 || ex is DirectoryNotFoundException ? ex?.Message : "See the service logs.", statusCode: status).ExecuteAsync(ctx);
}));

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/status", async (IAiProviderInfo ai, PlanviewIngestionJob job, IKnowledgeStore store, CancellationToken ct) =>
{
    var docs = await store.ListAsync(PlanviewConnector.VendorCode, DocumentStatus.Published, ct);
    return new
    {
        service = "Avantra Advisory Agent",
        mode = ai.ChatEnabled ? "agent (AI)" : "deterministic (no AI key)",
        model = ai.ChatModel,
        connectors = new[] { new { name = "Planview", index = job.Status().IndexName, search = job.Status().SearchProvider, publishedDocuments = docs.Count } },
        ingestion = job.Status(),
    };
});

api.MapPost("/chat", (ChatRequest r, ChatService chat, CancellationToken ct) => chat.ReplyAsync(r, ct));

var pv = api.MapGroup("/connectors/planview");
pv.MapGet("/products", (PlanviewConnector planview) => planview.ListProducts());
pv.MapGet("/ingest", (PlanviewIngestionJob job) => job.Status());
pv.MapPost("/ingest", (PlanviewIngestionJob job, CancellationToken ct) => job.RunAsync("api", ct))
  .WithSummary("Ingest the Planview source folder into planview-index now (unchanged files are skipped).");
pv.MapPost("/documents", async ([FromForm] IFormFile file, [FromForm] bool? publish, [FromForm] string? sourceUrl,
        [FromForm] SourceTrustLevel? trust, IngestionService ingestion, CancellationToken ct) =>
    {
        if (file.Length == 0 || file.Length > 25 * 1024 * 1024) return Results.BadRequest("File must be between 1 byte and 25 MB.");
        await using var stream = file.OpenReadStream();
        return Results.Ok(await ingestion.IngestAsync(new IngestionRequest(PlanviewConnector.VendorCode, file.FileName, file.ContentType, stream,
            SourceUrl: sourceUrl, Trust: trust ?? SourceTrustLevel.Unverified, PublishImmediately: publish ?? false), ct));
    })
  .DisableAntiforgery()
  .WithSummary("Upload one Planview document (PDF, DOCX, HTML, MD, TXT). Not published unless publish=true; suspicious files are quarantined.");
pv.MapPost("/documents/{id:guid}/publish", async (Guid id, IngestionService ingestion, CancellationToken ct) =>
    await ingestion.PublishAsync(id, ct) is { } d ? Results.Ok(new { d.Id, d.Title, d.Status }) : Results.NotFound());
pv.MapGet("/documents", async (IKnowledgeStore store, CancellationToken ct) =>
    (await store.ListAsync(PlanviewConnector.VendorCode, null, ct)).Select(d => new { d.Id, d.Title, d.Version, d.Status, d.StatusReason, d.IngestedAt }));

app.MapGet("/health", () => Results.Ok("ok"));
app.MapMcp("/mcp");

await app.Services.InitializeDatabaseAsync();
await app.RunAsync();

public partial class Program;
