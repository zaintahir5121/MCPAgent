using System.Text.Json;
using System.Text.Json.Serialization;
using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Domain.Catalogue;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Avantra.AIAdvisory.KnowledgeBase.Packs;

public sealed class KnowledgePackOptions
{
    public const string Section = "Knowledge";
    /// <summary>Folder containing one sub-folder per vendor with <c>pack.json</c> and <c>documents/</c>.</summary>
    public string PacksPath { get; set; } = "knowledge/vendors";
    /// <summary>InMemory (default) or AzureAiSearch.</summary>
    public string IndexProvider { get; set; } = "InMemory";
    public bool SeedDocumentsOnStartup { get; set; } = true;
}

/// <summary>
/// "Catalogue-as-code": each vendor pack is a versioned JSON file reviewed through pull requests. Loaded and
/// validated at startup. The SQL catalogue schema (docs/04) replaces this when the admin portal arrives.
/// </summary>
public sealed class FileSystemCatalogueRepository : ICatalogueRepository
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, VendorPack> _packs = new(StringComparer.OrdinalIgnoreCase);

    public FileSystemCatalogueRepository(IOptions<KnowledgePackOptions> options, ILogger<FileSystemCatalogueRepository> logger)
    {
        var root = ResolvePath(options.Value.PacksPath);
        PacksRoot = root;
        if (!Directory.Exists(root))
        {
            logger.LogWarning("Knowledge pack folder {Root} not found; no vendor domains loaded.", root);
            return;
        }
        foreach (var file in Directory.GetFiles(root, "pack.json", SearchOption.AllDirectories))
        {
            var pack = JsonSerializer.Deserialize<VendorPack>(File.ReadAllText(file), Json)
                       ?? throw new InvalidDataException($"Empty pack {file}");
            var errors = VendorPackValidator.Validate(pack);
            if (errors.Count > 0)
                throw new InvalidDataException($"Vendor pack {file} is invalid:\n - " + string.Join("\n - ", errors));
            _packs[pack.Vendor.Code] = pack;
            logger.LogInformation("Loaded vendor pack {Vendor} v{Version}: {Products} products, {Caps} capabilities",
                pack.Vendor.Code, pack.Vendor.PackVersion, pack.Products.Count, pack.Capabilities.Count);
        }
    }

    public string PacksRoot { get; }

    public IReadOnlyList<Vendor> GetVendors() => _packs.Values.Select(p => p.Vendor).OrderBy(v => v.Name).ToList();

    public VendorPack? GetPack(string vendorCode) => _packs.GetValueOrDefault(vendorCode ?? string.Empty);

    internal static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        // Walk up from the app base directory so it works from bin/, from the repo root and in containers.
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, path);
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
        }
        return Path.GetFullPath(path);
    }
}

/// <summary>Referential integrity + governance checks for a pack. A pack that fails never loads.</summary>
public static class VendorPackValidator
{
    public static IReadOnlyList<string> Validate(VendorPack pack)
    {
        var errors = new List<string>();
        if (pack.Vendor is null) return ["vendor is missing"];
        if (pack.Capabilities is null || pack.Products is null || pack.LicensingOptions is null || pack.FitRules is null || pack.ClarificationRules is null)
            return ["capabilities, products, licensingOptions, fitRules and clarificationRules must all be present (use [] when empty)"];

        var caps = pack.Capabilities.Select(c => c.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var products = pack.Products.Select(p => p.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dup in pack.Capabilities.GroupBy(c => c.Code).Where(g => g.Count() > 1)) errors.Add($"duplicate capability {dup.Key}");
        foreach (var dup in pack.Products.GroupBy(c => c.Code).Where(g => g.Count() > 1)) errors.Add($"duplicate product {dup.Key}");

        foreach (var p in pack.Products)
        {
            if (p.Sources is null || p.Sources.Count == 0) errors.Add($"product {p.Code} has no source reference (traceability rule)");
            foreach (var pc in p.Capabilities ?? [])
            {
                if (!caps.Contains(pc.CapabilityCode)) errors.Add($"product {p.Code} references unknown capability {pc.CapabilityCode}");
                if (pc.Evidence is null || pc.Evidence.Count == 0) errors.Add($"product {p.Code} capability {pc.CapabilityCode} has no evidence");
            }
        }
        foreach (var l in pack.LicensingOptions)
        {
            if (!products.Contains(l.ProductCode)) errors.Add($"licensing option {l.Code} references unknown product {l.ProductCode}");
            if (l.Disclosure == DisclosureStatus.PubliclyDocumented && (l.Sources is null || l.Sources.Count == 0))
                errors.Add($"licensing option {l.Code} claims public documentation but has no source");
            if (l.Disclosure != DisclosureStatus.PubliclyDocumented && !string.IsNullOrWhiteSpace(l.PublicTerms))
                errors.Add($"licensing option {l.Code} has publicTerms but is not PubliclyDocumented");
        }
        foreach (var r in pack.FitRules)
            foreach (var pc in r.ProductCodes)
                if (!products.Contains(pc)) errors.Add($"fit rule {r.Code} references unknown product {pc}");
        return errors;
    }
}
