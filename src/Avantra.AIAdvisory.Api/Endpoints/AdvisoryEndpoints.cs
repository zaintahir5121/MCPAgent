using Avantra.AIAdvisory.Agents;
using Avantra.AIAdvisory.Api.Security;
using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Application.Advisory;
using Avantra.AIAdvisory.Application.Knowledge;
using Avantra.AIAdvisory.Application.Reporting;
using Avantra.AIAdvisory.Domain.Catalogue;
using Avantra.AIAdvisory.Domain.Knowledge;
using Avantra.AIAdvisory.KnowledgeBase.Ingestion;
using Markdig;
using Microsoft.AspNetCore.Mvc;

namespace Avantra.AIAdvisory.Api.Endpoints;

public sealed record KnowledgeSearchRequest(string Query, int Top = 5, string? ProductCode = null, SourceTrustLevel MinimumTrust = SourceTrustLevel.Unverified);
public sealed record AnalyzeRequest(string VendorCode, string Requirements);
public sealed record RecommendRequest(string VendorCode, string Requirements, Guid? SessionId = null);
public sealed record GapRequest(string VendorCode, string Requirements, string[] ProductCodes);
public sealed record CompareRequest(string VendorCode, string[] ProductCodes, string[]? CapabilityCodes = null);
public sealed record AskRequest(string VendorCode, string Question);

public static class AdvisoryEndpoints
{
    private static readonly MarkdownPipeline Markdown = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    public static IEndpointRouteBuilder MapAdvisoryApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization(Policies.AdvisoryRead).WithTags("Advisory");

        // ---- Catalogue ----------------------------------------------------------------------------------------
        api.MapGet("/vendors", (ICatalogueRepository c) => c.GetVendors()).WithSummary("List vendor domains");

        api.MapGet("/vendors/{vendor}/capabilities", (string vendor, ICatalogueRepository c) =>
            c.GetPack(vendor) is { } p ? Results.Ok(p.Capabilities) : Results.NotFound()).WithSummary("Vendor capability model");

        api.MapGet("/vendors/{vendor}/products", (string vendor, ICatalogueRepository c) =>
            c.GetPack(vendor) is { } p ? Results.Ok(p.Products) : Results.NotFound()).WithSummary("Product catalogue");

        api.MapGet("/vendors/{vendor}/products/{code}", (string vendor, string code, ICatalogueRepository c) =>
            c.GetPack(vendor)?.Products.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase)) is { } p
                ? Results.Ok(new { product = p, licensing = c.GetPack(vendor)!.LicensingOptions.Where(l => l.ProductCode == p.Code) })
                : Results.NotFound());

        // ---- Knowledge ----------------------------------------------------------------------------------------
        api.MapPost("/vendors/{vendor}/knowledge/search", (string vendor, KnowledgeSearchRequest r, KnowledgeSearchService s, CancellationToken ct) =>
            s.SearchAsync(vendor, r.Query, r.Top, r.ProductCode, r.MinimumTrust, ct)).WithSummary("Hybrid knowledge search");

        var admin = api.MapGroup("/knowledge").RequireAuthorization(Policies.KnowledgeAdmin).WithTags("Knowledge administration");

        admin.MapGet("/documents", async (string? vendor, DocumentStatus? status, IKnowledgeStore store, CancellationToken ct) =>
            (await store.ListAsync(vendor, status, ct)).Select(d => new
            {
                d.Id, d.ExternalId, d.VendorCode, d.Title, d.SourceUrl, d.Version, d.Trust, d.Status, d.StatusReason,
                d.ProductCodes, d.IngestedAt, d.IngestedBy, d.IsDemonstrationData,
            }));

        admin.MapPost("/documents", async ([FromForm] string vendorCode, IFormFile file, [FromForm] string? sourceUrl,
                [FromForm] SourceTrustLevel? trust, [FromForm] string? title, [FromForm] bool? publish, IngestionService ingestion, CancellationToken ct) =>
            {
                if (file.Length == 0 || file.Length > 25 * 1024 * 1024) return Results.BadRequest("File must be between 1 byte and 25 MB.");
                await using var stream = file.OpenReadStream();
                var result = await ingestion.IngestAsync(new IngestionRequest(vendorCode, file.FileName, file.ContentType, stream,
                    Title: title, SourceUrl: sourceUrl, Trust: trust ?? SourceTrustLevel.Unverified, PublishImmediately: publish ?? false), ct);
                return Results.Ok(result);
            })
            .DisableAntiforgery()
            .WithSummary("Upload a document (PDF, DOCX, HTML, Markdown, TXT). Lands in PendingReview unless published by an admin.");

        admin.MapPost("/documents/{id:guid}/publish", async (Guid id, IngestionService ingestion, CancellationToken ct) =>
            await ingestion.PublishAsync(id, ct) is { } d ? Results.Ok(new { d.Id, d.Status, d.Version }) : Results.NotFound());

        // ---- Advisory -----------------------------------------------------------------------------------------
        var advisory = api.MapGroup("/advisory");

        advisory.MapPost("/analyze", (AnalyzeRequest r, AdvisoryOrchestrator o, CancellationToken ct) => o.AnalyzeAsync(r.VendorCode, r.Requirements, ct))
            .WithSummary("Extract requirement profile + clarification questions");

        advisory.MapPost("/recommend", (RecommendRequest r, AdvisoryOrchestrator o, CancellationToken ct) =>
                o.AdviseAsync(new AdvisoryRequest(r.VendorCode, r.Requirements, r.SessionId), ct))
            .WithSummary("Full advisory run (creates or continues a session)");

        advisory.MapPost("/gaps", (GapRequest r, AdvisoryOrchestrator o, CancellationToken ct) => o.AnalyzeGapsAsync(r.VendorCode, r.Requirements, r.ProductCodes, ct))
            .WithSummary("Gap analysis for a given product set");

        advisory.MapPost("/compare", (CompareRequest r, ProductComparisonService s) => s.Compare(r.VendorCode, r.ProductCodes, r.CapabilityCodes))
            .WithSummary("Compare products and licensing options");

        advisory.MapPost("/ask", (AskRequest r, KnowledgeAssistantAgent a, CancellationToken ct) => a.AskAsync(r.VendorCode, r.Question, ct))
            .WithSummary("Grounded Q&A (Microsoft Agent Framework)");

        advisory.MapGet("/sessions", async (ISessionStore store, IUserContext user, int? take, CancellationToken ct) =>
            (await store.ListAsync(user.TenantId, user.IsInRole(Roles.AdvisoryReviewer) ? null : user.UserId, take ?? 20, ct))
            .Select(s => new { s.Id, s.VendorCode, s.Channel, s.CreatedAt, s.UpdatedAt, s.Review, turns = s.Turns.Count, s.OwnerId }));

        advisory.MapGet("/sessions/{id:guid}", async (Guid id, AdvisoryOrchestrator o, CancellationToken ct) =>
            await o.GetLatestAsync(id, ct) is { } r ? Results.Ok(r) : Results.NotFound());

        advisory.MapPost("/sessions/{id:guid}/review", async (Guid id, ReviewDecision d, AdvisoryOrchestrator o, CancellationToken ct) =>
                await o.ReviewAsync(id, d, ct) is { } r ? Results.Ok(new { r.SessionId, r.Review }) : Results.NotFound())
            .RequireAuthorization(Policies.AdvisoryReview)
            .WithSummary("Human approval gate for customer-shareable reports");

        advisory.MapGet("/sessions/{id:guid}/report", async (Guid id, string? format, string? customerName, AdvisoryOrchestrator o,
                ICatalogueRepository c, AdvisoryReportGenerator g, CancellationToken ct) =>
            {
                var result = await o.GetLatestAsync(id, ct);
                if (result is null) return Results.NotFound();
                var report = g.Generate(result, c.GetPack(result.VendorCode)?.Vendor.Name ?? result.VendorCode, customerName);
                return (format ?? "md").ToLowerInvariant() switch
                {
                    "html" => Results.Content(Html(report), "text/html"),
                    "json" => Results.Ok(report),
                    _ => Results.File(System.Text.Encoding.UTF8.GetBytes(report.Markdown), "text/markdown", $"advisory-{id:N}.md"),
                };
            })
            .WithSummary("Advisory report (md | html | json)");

        return app;
    }

    internal static string Html(AdvisoryReport report) => $$$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>{{{System.Net.WebUtility.HtmlEncode(report.Title)}}}</title>
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <style>
          body{font-family:"Segoe UI",system-ui,sans-serif;max-width:960px;margin:2rem auto;padding:0 1rem;color:#1b1f24;line-height:1.5}
          h1{color:#0b3d91;border-bottom:3px solid #0b3d91;padding-bottom:.3rem} h2{color:#0b3d91;margin-top:2rem}
          table{border-collapse:collapse;width:100%;margin:1rem 0;font-size:.92rem} th,td{border:1px solid #d0d7de;padding:.4rem .6rem;text-align:left;vertical-align:top}
          th{background:#f3f6fb} blockquote{background:#fff4e5;border-left:4px solid #f59e0b;margin:1rem 0;padding:.6rem 1rem}
          code{background:#f3f4f6;padding:0 .25rem;border-radius:3px} @media print{body{margin:0}}
        </style></head><body>{{{Markdig.Markdown.ToHtml(report.Markdown, Markdown)}}}</body></html>
        """;
}
