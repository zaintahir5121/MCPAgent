using Avantra.AIAdvisory.Application.Evidence;
using Avantra.AIAdvisory.Application.Licensing;
using Avantra.AIAdvisory.Application.Recommendation;
using Avantra.AIAdvisory.Application.Requirements;
using Avantra.AIAdvisory.Application.Safety;
using Avantra.AIAdvisory.Domain.Advisory;
using Avantra.AIAdvisory.Domain.Catalogue;
using Avantra.AIAdvisory.Domain.Knowledge;

namespace Avantra.AIAdvisory.UnitTests;

public class RecommendationTests
{
    private static async Task<RequirementProfile> Profile(string text) =>
        (await new HeuristicRequirementsExtractor().ExtractAsync(text, TestPack.Planview, default))!;

    [Fact]
    public async Task Example_scenario_recommends_planview_portfolios_with_full_must_have_coverage()
    {
        var options = new RecommendationEngine().Recommend(await Profile(TestPack.ExampleScenario), TestPack.Planview);

        var top = options[0];
        Assert.Equal(["planview-portfolios"], top.ProductCodes);
        Assert.Equal(1.0, top.MustHaveCoverage);
        Assert.Empty(top.Gaps);
        Assert.True(options.Count > 1, "alternatives should be offered");
        Assert.All(options.Skip(1), o => Assert.True(o.Score <= top.Score));
    }

    [Fact]
    public async Task Engine_is_deterministic()
    {
        var p = await Profile(TestPack.ExampleScenario);
        var a = new RecommendationEngine().Recommend(p, TestPack.Planview);
        var b = new RecommendationEngine().Recommend(p, TestPack.Planview);
        Assert.Equal(a.Select(o => string.Join("+", o.ProductCodes)), b.Select(o => string.Join("+", o.ProductCodes)));
        Assert.Equal(a.Select(o => o.Score), b.Select(o => o.Score));
    }

    [Fact]
    public async Task Hybrid_delivery_applies_avantra_rule_and_adds_agile_layer()
    {
        var p = await Profile(TestPack.ExampleScenario + " We run a hybrid model: PMO stage-gates plus Scrum teams.");
        var top = new RecommendationEngine().Recommend(p, TestPack.Planview)[0];
        Assert.Contains("planview-agileplace", top.ProductCodes);
        Assert.Contains(top.AppliedRules, r => r.Code == "avr-agile-portfolio-connect" && r.RequiresBusinessConfirmation);
    }

    [Fact]
    public async Task Uncovered_requirement_is_reported_as_gap_not_hidden()
    {
        var p = await Profile("We must have Kanban boards and innovation management.");
        var eval = new RecommendationEngine().EvaluateProducts(p, TestPack.Planview, ["planview-agileplace"]);
        Assert.Contains(eval.Gaps, g => g.CapabilityCode == "innovation-management");
        Assert.Equal(0.5, eval.MustHaveCoverage);
    }

    [Fact]
    public void No_requirements_means_no_recommendation()
    {
        var p = new RequirementProfile("planview", new OrganizationProfile(), [], [], "heuristic");
        Assert.Empty(new RecommendationEngine().Recommend(p, TestPack.Planview));
    }

    [Fact]
    public void Evidence_is_never_verified_while_catalogue_is_pending_curation()
    {
        var product = TestPack.Planview.Products.First(p => p.Code == "planview-portfolios");
        var pc = product.GetCapability("portfolio-management");
        Assert.Equal(EvidenceGrade.CatalogueUnverified, EvidenceService.Grade(product, pc, []));
        Assert.Equal(EvidenceGrade.Unsupported, EvidenceService.Grade(product, null, []));

        var verified = product with { Verification = VerificationStatus.Verified };
        var official = pc! with { Evidence = [pc.Evidence[0] with { Trust = SourceTrustLevel.VendorOfficial }] };
        Assert.Equal(EvidenceGrade.Verified, EvidenceService.Grade(verified, official, []));
    }

    [Fact]
    public async Task Licensing_never_contains_prices_and_requires_vendor_confirmation()
    {
        var p = await Profile(TestPack.ExampleScenario);
        var result = new LicensingEvaluator().Evaluate(["planview-portfolios", "planview-agileplace"], p, TestPack.Planview, new CitationRegistry());

        Assert.All(result, l =>
        {
            Assert.True(l.RequiresVendorConfirmation);
            Assert.False(ContentSafety.ContainsPriceStatement(l.Statement));
            Assert.All(l.KnownOptions, o => Assert.Null(o.PublicTerms));
        });
        var sizing = result[0].SizingInputs;
        Assert.Contains(sizing, s => s.Persona == "Project managers" && s.Count == 150);
        Assert.Contains(sizing, s => s.Persona == "Portfolio managers" && s.Count == 40);
    }

    [Fact]
    public void Citation_registry_deduplicates_by_document_and_section()
    {
        var reg = new CitationRegistry();
        var s = new SourceReference("doc1", "Doc", null, "Overview", SourceTrustLevel.VendorOfficial, null);
        Assert.Equal(reg.Add(s), reg.Add(s));
        Assert.Single(reg.All);
    }
}
