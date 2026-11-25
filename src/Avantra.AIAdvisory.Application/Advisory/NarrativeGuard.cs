using System.Text.RegularExpressions;
using Avantra.AIAdvisory.Application.Safety;
using Avantra.AIAdvisory.Domain.Catalogue;

namespace Avantra.AIAdvisory.Application.Advisory;

/// <summary>
/// Post-generation validation of AI-written narrative. Rejects text that states prices, names products that are not
/// in the vendor catalogue, or cites citation ids that do not exist. Rejected text is replaced by a deterministic summary.
/// </summary>
public static partial class NarrativeGuard
{
    [GeneratedRegex(@"\[(C\d+)\]")]
    private static partial Regex CitationRef();

    public static bool IsAcceptable(string narrative, VendorPack pack, IReadOnlyCollection<string> citationIds, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(narrative)) { reason = "empty"; return false; }
        if (ContentSafety.ContainsPriceStatement(narrative)) { reason = "contains price statement"; return false; }

        foreach (Match m in CitationRef().Matches(narrative))
            if (!citationIds.Contains(m.Groups[1].Value)) { reason = $"unknown citation {m.Value}"; return false; }

        var vendor = Regex.Escape(pack.Vendor.Name);
        var known = pack.Products.SelectMany(p => p.Aliases.Append(p.Name)).Select(n => n.ToLowerInvariant()).ToHashSet();
        foreach (Match m in Regex.Matches(narrative, $@"\b{vendor}\s+([A-Z][A-Za-z0-9]+(?:\s[A-Z][A-Za-z0-9]+)?)"))
        {
            var candidate = m.Value.ToLowerInvariant();
            if (!known.Any(k => candidate.StartsWith(k) || k.StartsWith(candidate)))
            {
                reason = $"mentions product not in catalogue: '{m.Value}'";
                return false;
            }
        }
        return true;
    }
}
