using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Application.Evidence;
using Avantra.AIAdvisory.Application.Knowledge;
using Avantra.AIAdvisory.Application.Licensing;
using Avantra.AIAdvisory.Domain.Advisory;
using Avantra.AIAdvisory.Domain.Catalogue;

namespace Avantra.AIAdvisory.Application.Advisory;

/// <summary>Side-by-side capability and licensing comparison from catalogue data only.</summary>
public sealed class ProductComparisonService(ICatalogueRepository catalogue, LicensingEvaluator licensing)
{
    public ProductComparison Compare(string vendorCode, IReadOnlyList<string> productCodes, IReadOnlyList<string>? capabilityCodes = null)
    {
        var pack = catalogue.GetPack(vendorCode) ?? throw new UnknownVendorException(vendorCode);
        var products = productCodes.Select(c => pack.Products.FirstOrDefault(p => string.Equals(p.Code, c, StringComparison.OrdinalIgnoreCase)
                                                                                || p.Aliases.Contains(c, StringComparer.OrdinalIgnoreCase)
                                                                                || string.Equals(p.Name, c, StringComparison.OrdinalIgnoreCase)))
            .Where(p => p is not null).Select(p => p!).DistinctBy(p => p.Code).ToList();
        if (products.Count == 0) throw new ArgumentException("None of the requested products exist in the catalogue.");

        var caps = (capabilityCodes is { Count: > 0 }
                ? pack.Capabilities.Where(c => capabilityCodes.Contains(c.Code, StringComparer.OrdinalIgnoreCase))
                : pack.Capabilities.Where(c => products.Any(p => p.GetCapability(c.Code) is not null)))
            .ToList();

        var rows = caps.Select(c => new ProductComparisonRow(c.Code, c.Name,
            products.ToDictionary(p => p.Code, p => p.GetCapability(c.Code)?.Coverage.ToString() ?? "Not listed"))).ToList();

        var registry = new CitationRegistry();
        foreach (var src in products.SelectMany(p => p.Sources)) registry.Add(src, demo: pack.Vendor.IsDemonstrationData);
        var profile = new RequirementProfile(pack.Vendor.Code, new OrganizationProfile(), [], [], "none");
        var lic = licensing.Evaluate(products.Select(p => p.Code), profile, pack, registry);

        return new ProductComparison(pack.Vendor.Code, products.Select(p => p.Code).ToList(), products.Select(p => p.Name).ToList(),
            rows, lic, registry.All,
            "'Not listed' means the knowledge base has no evidence for the capability - not that the product lacks it. " + pack.Vendor.DataDisclaimer);
    }
}
