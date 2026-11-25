namespace Avantra.AIAdvisory.Domain.Catalogue;

/// <summary>
/// How much we trust the origin of a fact. Drives ranking, citation display and whether a fact may be
/// presented as "verified" to a customer. Higher is more trusted.
/// </summary>
public enum SourceTrustLevel
{
    Unverified = 0,
    ThirdParty = 1,
    AvantraInternal = 2,
    PartnerVerified = 3,
    VendorOfficial = 4,
}

/// <summary>
/// Curation state of a catalogue fact. Only <see cref="Verified"/> facts can be stated without a caveat.
/// </summary>
public enum VerificationStatus
{
    /// <summary>Entered from a source but not yet checked by a knowledge curator.</summary>
    PendingCuratorVerification = 0,
    /// <summary>Checked by a curator against the cited source on <c>VerifiedOn</c>.</summary>
    Verified = 1,
    /// <summary>Known to be out of date or superseded; kept for history, never used in recommendations.</summary>
    Deprecated = 2,
}

/// <summary>How strongly a product delivers a capability, per the cited source.</summary>
public enum CoverageLevel
{
    /// <summary>Delivered via integration or another product; not native.</summary>
    ViaIntegration = 1,
    /// <summary>Supported, but not a primary design focus of the product.</summary>
    Supported = 2,
    /// <summary>Core, primary capability of the product.</summary>
    Core = 3,
}

/// <summary>Whether licensing/pricing information is publicly available.</summary>
public enum DisclosureStatus
{
    /// <summary>No public information. Must be confirmed with the vendor or an authorised partner.</summary>
    NotPublic = 0,
    /// <summary>Available to Avantra under a partner agreement; never shown to customers without approval.</summary>
    PartnerConfidential = 1,
    /// <summary>Publicly documented by the vendor (cite the source).</summary>
    PubliclyDocumented = 2,
}

public sealed record SourceReference(
    string DocumentId,
    string Title,
    string? Url,
    string? Section,
    SourceTrustLevel Trust,
    DateOnly? RetrievedOn);

public sealed record Vendor(
    string Code,
    string Name,
    string Description,
    string PackVersion,
    string DataDisclaimer,
    bool IsDemonstrationData = false);

public sealed record Capability(
    string Code,
    string VendorCode,
    string Name,
    string Category,
    string Description,
    IReadOnlyList<string> Synonyms);

public sealed record ProductCapability(
    string CapabilityCode,
    CoverageLevel Coverage,
    string? Notes,
    IReadOnlyList<SourceReference> Evidence);

public sealed record Product(
    string Code,
    string VendorCode,
    string Name,
    IReadOnlyList<string> Aliases,
    string Summary,
    IReadOnlyList<string> Solutions,
    IReadOnlyList<string> DeploymentModels,
    IReadOnlyList<ProductCapability> Capabilities,
    IReadOnlyList<SourceReference> Sources,
    VerificationStatus Verification,
    DateOnly? VerifiedOn,
    IReadOnlyList<string> ImplementationConsiderations)
{
    public ProductCapability? GetCapability(string capabilityCode) =>
        Capabilities.FirstOrDefault(c => string.Equals(c.CapabilityCode, capabilityCode, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A licensing / subscription option. Deliberately carries no price field: prices are never stored
/// unless <see cref="DisclosureStatus.PubliclyDocumented"/> and then only as cited text in <see cref="PublicTerms"/>.
/// </summary>
public sealed record LicensingOption(
    string Code,
    string ProductCode,
    string Name,
    string Description,
    DisclosureStatus Disclosure,
    string LicenseMetric,
    IReadOnlyList<string> SizingDrivers,
    string? PublicTerms,
    IReadOnlyList<SourceReference> Sources)
{
    public bool RequiresVendorConfirmation => Disclosure != DisclosureStatus.PubliclyDocumented;
}

/// <summary>
/// An Avantra advisory heuristic (NOT a vendor fact). Boosts or penalises candidate solutions when a
/// condition about the customer profile holds. Always surfaced to the user as an Avantra rule.
/// </summary>
public sealed record FitRule(
    string Code,
    string Description,
    FitCondition When,
    IReadOnlyList<string> ProductCodes,
    double ScoreAdjustment,
    string Rationale,
    bool RequiresBusinessConfirmation);

public sealed record FitCondition(
    string? Methodology = null,
    int? MinEmployees = null,
    int? MaxEmployees = null,
    string? CapabilityPresent = null,
    string? DeploymentPreference = null);

/// <summary>A data-driven clarification rule: if a profile field is missing, ask this.</summary>
public sealed record ClarificationRule(
    string Code,
    string Field,
    string Question,
    string WhyItMatters,
    int Priority,
    string? OnlyIfCapability = null);

/// <summary>The full, versioned knowledge pack of one vendor domain.</summary>
public sealed record VendorPack(
    Vendor Vendor,
    IReadOnlyList<Capability> Capabilities,
    IReadOnlyList<Product> Products,
    IReadOnlyList<LicensingOption> LicensingOptions,
    IReadOnlyList<FitRule> FitRules,
    IReadOnlyList<ClarificationRule> ClarificationRules);
