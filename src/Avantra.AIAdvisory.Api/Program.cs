using System.Text.Json.Serialization;
using Avantra.AIAdvisory.Agents;
using Avantra.AIAdvisory.Api.Endpoints;
using Avantra.AIAdvisory.Api.Security;
using Avantra.AIAdvisory.Application;
using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Application.Knowledge;
using Avantra.AIAdvisory.Infrastructure;
using Avantra.AIAdvisory.KnowledgeBase;
using Avantra.AIAdvisory.KnowledgeBase.Ingestion;
using Avantra.AIAdvisory.McpServer;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration: appsettings -> appsettings.{env} -> user-secrets (dev) -> environment variables -> Key Vault (prod)
if (builder.Configuration["KeyVault:Uri"] is { Length: > 0 } kv)
    builder.Configuration.AddAzureKeyVault(new Uri(kv), new Azure.Identity.DefaultAzureCredential());

// ---- Observability: OpenTelemetry -> Azure Monitor / Application Insights when a connection string is present
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
builder.Services.AddHealthChecks();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .AllowAnyHeader().AllowAnyMethod()));

// ---- Layers
builder.Services
    .AddAdvisorySecurity(builder.Configuration)
    .AddAdvisoryApplication()
    .AddAdvisoryInfrastructure(builder.Configuration)
    .AddKnowledgePlatform(builder.Configuration)
    .AddAdvisoryAgents()
    .AddAdvisoryMcpServer();

var app = builder.Build();

app.UseExceptionHandler(e => e.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = ex switch
    {
        UnknownVendorException => (StatusCodes.Status404NotFound, "Unknown vendor domain"),
        ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request"),
        UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "Forbidden"),
        NotSupportedException => (StatusCodes.Status415UnsupportedMediaType, "Unsupported document type"),
        _ => (StatusCodes.Status500InternalServerError, "Unexpected error"),
    };
    ctx.Response.StatusCode = status;
    await Results.Problem(title: title, detail: status < 500 ? ex?.Message : "See logs with the trace id.", statusCode: status,
        extensions: new Dictionary<string, object?> { ["traceId"] = System.Diagnostics.Activity.Current?.Id ?? ctx.TraceIdentifier }).ExecuteAsync(ctx);
}));

app.UseHttpsRedirection();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi().AllowAnonymous();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapGet("/", (IAiProviderInfo ai, ICatalogueRepository c) => Results.Ok(new
{
    service = "Avantra AI Advisory Platform",
    vendors = c.GetVendors().Select(v => v.Code),
    ai = new { chat = ai.ChatEnabled, embeddings = ai.EmbeddingsEnabled, model = ai.ChatModel, mode = ai.ChatEnabled ? "ai-assisted" : "deterministic-only" },
    endpoints = new { openApi = "/openapi/v1.json", mcp = "/mcp", api = "/api/v1" },
})).AllowAnonymous();

app.MapAdvisoryApi();
app.MapAdvisoryMcp("/mcp").RequireAuthorization(Policies.AdvisoryRead);

await app.Services.InitializeDatabaseAsync();

// Container Apps Job mode: run the ingestion/seeding pipeline once and exit (no HTTP server).
if (args.Contains("--ingest-only"))
{
    var bootstrapper = ActivatorUtilities.CreateInstance<KnowledgeBootstrapper>(app.Services);
    await bootstrapper.StartAsync(CancellationToken.None);
    return;
}

await app.RunAsync();

public partial class Program;
