using Avantra.AIAdvisory.Application.Evidence;
using Avantra.AIAdvisory.Domain.Advisory;
using Avantra.AIAdvisory.Domain.Catalogue;

namespace Avantra.AIAdvisory.Application.Licensing;

/// <summary>
/// Evaluates licensing/subscription considerations WITHOUT inventing prices or tiers. It returns the options the
/// catalogue knows about (with disclosure status) and sizing inputs derived from the customer's own numbers.
/// </summary>
public sealed class LicensingEvaluator
{
    private static readonly Dictionary<string, Func<OrganizationProfile, int?>> Personas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["employees"] = o => o.Employees,
        ["projectManagers"] = o => o.ProjectManagers,
        ["portfolioManagers"] = o => o.PortfolioManagers,
        ["resourceManagers"] = o => o.ResourceManagers,
        ["teamMembers"] = o => o.TeamMembers,
        ["executives"] = o => o.Executives,
        ["businessUnits"] = o => o.BusinessUnits,
    };

    public IReadOnlyList<LicensingAssessment> Evaluate(IEnumerable<string> productCodes, RequirementProfile profile, VendorPack pack, CitationRegistry registry)
    {
        var result = new List<LicensingAssessment>();
        foreach (var code in productCodes.Distinct())
        {
            var product = pack.Products.FirstOrDefault(p => p.Code == code);
            if (product is null) continue;
            var options = pack.LicensingOptions.Where(l => l.ProductCode == code).ToList();
            var drivers = options.SelectMany(o => o.SizingDrivers).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (drivers.Count == 0) drivers = ["employees"];

            var sizing = drivers.Select(d => new LicensingSizingInput(
                Humanize(d),
                Personas.TryGetValue(d, out var f) ? f(profile.Organization) : null,
                Personas.TryGetValue(d, out var g) && g(profile.Organization) is not null
                    ? "Stated by customer - a sizing input, not a licence quantity."
                    : "Unknown - required to size the subscription.")).ToList();

            var citationIds = options.SelectMany(o => o.Sources).Select(s => registry.Add(s, demo: pack.Vendor.IsDemonstrationData)).Distinct().ToList();
            var requiresConfirmation = options.Count == 0 || options.Any(o => o.RequiresVendorConfirmation);
            var statement = options.Count == 0
                ? $"No licensing information for {product.Name} is held in the knowledge base. Licensing model, editions and pricing must be confirmed with {pack.Vendor.Name} or an authorised partner."
                : requiresConfirmation
                    ? $"{pack.Vendor.Name} does not publicly document complete licensing terms for {product.Name} in the sources held. Treat the options below as orientation only and confirm licence types, editions, quantities and pricing with {pack.Vendor.Name} or an authorised partner."
                    : $"Licensing terms for {product.Name} are publicly documented in the cited sources.";

            result.Add(new LicensingAssessment(product.Code, product.Name, options, sizing, requiresConfirmation, statement, citationIds));
        }
        return result;
    }

    private static string Humanize(string driver) => driver switch
    {
        "employees" => "Total employees (upper bound for occasional/read-only users)",
        "projectManagers" => "Project managers",
        "portfolioManagers" => "Portfolio managers",
        "resourceManagers" => "Resource managers",
        "teamMembers" => "Team members / contributors",
        "executives" => "Executives / stakeholders",
        "businessUnits" => "Business units",
        _ => driver,
    };
}
