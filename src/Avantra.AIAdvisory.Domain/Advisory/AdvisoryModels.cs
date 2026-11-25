using Avantra.AIAdvisory.Domain.Catalogue;

namespace Avantra.AIAdvisory.Domain.Advisory;

public enum RequirementPriority { Could = 1, Should = 2, Must = 3 }

/// <summary>Where a requirement came from - stated by the customer, or inferred by the analyser.</summary>
public enum RequirementOrigin { Stated, Inferred }

public sealed record Requirement(
    string Id,
    string Statement,
    string CapabilityCode,
    RequirementPriority Priority,
    RequirementOrigin Origin);

public sealed record OrganizationProfile
{
    public string? OrganizationName { get; init; }
    public string? Industry { get; init; }
    public int? Employees { get; init; }
    public int? ProjectManagers { get; init; }
    public int? PortfolioManagers { get; init; }
    public int? ResourceManagers { get; init; }
    public int? TeamMembers { get; init; }
    public int? Executives { get; init; }
    public int? BusinessUnits { get; init; }
    /// <summary>traditional | agile | hybrid</summary>
    public string? Methodology { get; init; }
    /// <summary>saas | on-premises | either</summary>
    public string? DeploymentPreference { get; init; }
    public IReadOnlyList<string> ExistingSystems { get; init; } = [];
    public IReadOnlyList<string> Regions { get; init; } = [];
}

public sealed record RequirementProfile(
    string VendorCode,
    OrganizationProfile Organization,
    IReadOnlyList<Requirement> Requirements,
    IReadOnlyList<string> UnmappedStatements,
    string AnalyzerUsed);

public sealed record ClarificationQuestion(string Code, string Question, string WhyItMatters, int Priority);

public enum EvidenceGrade
{
    /// <summary>No source supports this claim. Must never be shown as fact.</summary>
    Unsupported = 0,
    /// <summary>Supported by catalogue data that has not been curator-verified.</summary>
    CatalogueUnverified = 1,
    /// <summary>Supported by catalogue data and retrieved document passage(s), not yet curator-verified.</summary>
    DocumentSupported = 2,
    /// <summary>Curator-verified catalogue entry with official source.</summary>
    Verified = 3,
}

public sealed record Citation(
    string Id,
    string DocumentTitle,
    string? Url,
    string? Section,
    string? Snippet,
    SourceTrustLevel Trust,
    bool IsDemonstrationData);

public sealed record RequirementCoverage(
    Requirement Requirement,
    string? ProductCode,
    CoverageLevel? Coverage,
    EvidenceGrade Evidence,
    IReadOnlyList<string> CitationIds,
    string Explanation);

public sealed record AppliedRule(string Code, string Description, double Adjustment, string Rationale, bool RequiresBusinessConfirmation);

public sealed record SolutionOption(
    string OptionId,
    int Rank,
    string Label,
    IReadOnlyList<string> ProductCodes,
    IReadOnlyList<string> ProductNames,
    double Score,
    double MustHaveCoverage,
    double WeightedCoverage,
    IReadOnlyList<RequirementCoverage> Coverage,
    IReadOnlyList<Requirement> Gaps,
    IReadOnlyList<AppliedRule> AppliedRules,
    IReadOnlyList<string> Considerations);

public sealed record LicensingSizingInput(string Persona, int? Count, string Basis);

public sealed record LicensingAssessment(
    string ProductCode,
    string ProductName,
    IReadOnlyList<LicensingOption> KnownOptions,
    IReadOnlyList<LicensingSizingInput> SizingInputs,
    bool RequiresVendorConfirmation,
    string Statement,
    IReadOnlyList<string> CitationIds);

public enum ReviewStatus { Draft = 0, Approved = 1, Rejected = 2 }

public sealed record AdvisoryStatement(string Kind, string Text);

/// <summary>The complete, explainable output of one advisory run.</summary>
public sealed record AdvisoryResult(
    Guid SessionId,
    string VendorCode,
    DateTimeOffset GeneratedAt,
    RequirementProfile Profile,
    IReadOnlyList<ClarificationQuestion> Clarifications,
    IReadOnlyList<SolutionOption> Options,
    IReadOnlyList<LicensingAssessment> Licensing,
    IReadOnlyList<Citation> Citations,
    IReadOnlyList<AdvisoryStatement> Assumptions,
    IReadOnlyList<AdvisoryStatement> Warnings,
    string? ExecutiveSummary,
    string ExecutiveSummarySource,
    ReviewStatus Review,
    string DataDisclaimer);
