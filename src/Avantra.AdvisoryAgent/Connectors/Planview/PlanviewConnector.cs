using System.Text.Json;
using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Application.Advisory;
using Avantra.AIAdvisory.Application.Evidence;
using Avantra.AIAdvisory.Application.Knowledge;
using Avantra.AIAdvisory.Application.Licensing;
using Avantra.AIAdvisory.Domain.Advisory;
using Avantra.AIAdvisory.Domain.Catalogue;

namespace Avantra.AdvisoryAgent.Connectors.Planview;

public sealed class PlanviewConnectorOptions
{
    public const string Section = "Connectors:Planview";
    /// <summary>Folder with Planview source documents (PDF, DOCX, HTML, MD, TXT) ingested into planview-index.</summary>
    public string SourceFolder { get; set; } = "knowledge/vendors/planview/documents";
    public bool RunIngestionOnStartup { get; set; } = true;
    /// <summary>Scheduled re-ingestion interval. 0 disables the schedule.</summary>
    public double IngestionIntervalHours { get; set; } = 24;
    public PlanviewApiOptions Api { get; set; } = new();
}

/// <summary>
/// The Planview connector: every Planview capability the agent, the chat page and MCP clients can use.
/// It only ever touches the Planview knowledge pack and the Planview index.
/// </summary>
public sealed class PlanviewConnector(
    AdvisoryOrchestrator orchestrator,
    KnowledgeSearchService search,
    ProductComparisonService comparison,
    LicensingEvaluator licensing,
    ICatalogueRepository catalogue)
{
    public const string VendorCode = "planview";

    public VendorPack Pack => catalogue.GetPack(VendorCode) ?? throw new InvalidOperationException("Planview knowledge pack is not loaded.");

    public Task<KnowledgeSearchResult> SearchAsync(string query, int top = 5, string? productCode = null, CancellationToken ct = default) =>
        search.SearchAsync(VendorCode, query, top, productCode, SourceTrustLevel.Unverified, ct);

    public Task<AdvisoryResult> RecommendAsync(string requirements, Guid? sessionId, CancellationToken ct = default) =>
        orchestrator.AdviseAsync(new AdvisoryRequest(VendorCode, requirements, sessionId), ct);

    public ProductComparison Compare(IReadOnlyList<string> products) => comparison.Compare(VendorCode, products);

    public IReadOnlyList<LicensingAssessment> Licensing(IReadOnlyList<string> products, OrganizationProfile organization) =>
        licensing.Evaluate(ResolveCodes(products), new RequirementProfile(VendorCode, organization, [], [], "none"), Pack, new CitationRegistry());

    public IReadOnlyList<object> ListProducts() => Pack.Products.Select(p => (object)new
    {
        p.Code, p.Name, p.Summary, verification = p.Verification.ToString(),
        capabilities = p.Capabilities.Select(c => $"{c.CapabilityCode} ({c.Coverage})"),
    }).ToList();

    /// <summary>Finds product codes mentioned by code, name or alias in free text.</summary>
    public IReadOnlyList<string> FindProducts(string text) =>
        Pack.Products.Where(p => p.Aliases.Append(p.Name).Append(p.Code)
                .Any(a => a.Contains(' ') || a.Contains('-')
                    ? text.Contains(a, StringComparison.OrdinalIgnoreCase)
                    : System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{System.Text.RegularExpressions.Regex.Escape(a)}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            .Select(p => p.Code).Distinct().ToList();

    private List<string> ResolveCodes(IEnumerable<string> products) =>
        products.SelectMany(p => FindProducts(p).DefaultIfEmpty(p)).Distinct().ToList();

    public static string Json(object value) => JsonSerializer.Serialize(value, AdvisoryOrchestrator.Json);
}
