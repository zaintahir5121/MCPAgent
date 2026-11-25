using System.Text.Json;
using Avantra.AIAdvisory.Domain.Catalogue;
using Avantra.AIAdvisory.KnowledgeBase.Packs;

namespace Avantra.AIAdvisory.UnitTests;

/// <summary>Loads the real Planview pack from the repository so tests guard the shipped data, not a copy.</summary>
public static class TestPack
{
    private static readonly Lazy<VendorPack> Lazy = new(() =>
    {
        var root = FileSystemCatalogueRepository.ResolvePath("knowledge/vendors");
        return JsonSerializer.Deserialize<VendorPack>(File.ReadAllText(Path.Combine(root, "planview", "pack.json")), FileSystemCatalogueRepository.Json)!;
    });

    public static VendorPack Planview => Lazy.Value;

    public const string ExampleScenario =
        "We are a company with 2,000 employees, 150 project managers, 40 portfolio managers, and multiple business units. " +
        "We need enterprise portfolio management, strategic planning, resource management, financial management, and project governance. " +
        "Which Planview products, subscriptions, plans, and licensing tiers would you recommend?";
}
