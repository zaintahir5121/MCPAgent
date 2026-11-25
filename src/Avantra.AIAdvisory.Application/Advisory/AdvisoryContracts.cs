using Avantra.AIAdvisory.Domain.Advisory;
using Avantra.AIAdvisory.Domain.Catalogue;

namespace Avantra.AIAdvisory.Application.Advisory;

public sealed record AdvisoryRequest(string VendorCode, string Requirements, Guid? SessionId = null);

public sealed record ProductComparisonRow(string CapabilityCode, string CapabilityName, IReadOnlyDictionary<string, string> CoverageByProduct);

public sealed record ProductComparison(
    string VendorCode,
    IReadOnlyList<string> ProductCodes,
    IReadOnlyList<string> ProductNames,
    IReadOnlyList<ProductComparisonRow> Rows,
    IReadOnlyList<LicensingAssessment> Licensing,
    IReadOnlyList<Citation> Citations,
    string Notice);

public sealed record ReviewDecision(bool Approve, string? Comment);

public sealed record AdvisoryReport(Guid SessionId, string Title, string Markdown, ReviewStatus Review, DateTimeOffset GeneratedAt);

public sealed record RequirementAnalysis(RequirementProfile Profile, IReadOnlyList<ClarificationQuestion> Clarifications, IReadOnlyList<Capability> MatchedCapabilities);

public sealed record GapAnalysis(
    RequirementProfile Profile,
    SolutionOption Evaluation,
    IReadOnlyList<string> UnknownProductCodes,
    IReadOnlyList<string> SuggestedAdditionalProducts,
    IReadOnlyList<Citation> Citations,
    string Notice);
