using Avantra.AIAdvisory.Application.Requirements;
using Avantra.AIAdvisory.Domain.Advisory;
using Avantra.AIAdvisory.Domain.Catalogue;
using Microsoft.Extensions.Logging.Abstractions;

namespace Avantra.AIAdvisory.UnitTests;

public class RequirementsTests
{
    [Fact]
    public async Task Heuristic_extracts_headcount_and_must_have_capabilities()
    {
        var profile = (await new HeuristicRequirementsExtractor().ExtractAsync(TestPack.ExampleScenario, TestPack.Planview, default))!;

        Assert.Equal(2000, profile.Organization.Employees);
        Assert.Equal(150, profile.Organization.ProjectManagers);
        Assert.Equal(40, profile.Organization.PortfolioManagers);
        Assert.Null(profile.Organization.BusinessUnits); // "multiple" is not a number - must be clarified, not guessed
        foreach (var cap in new[] { "portfolio-management", "strategic-planning", "resource-management", "financial-management", "project-governance" })
            Assert.Contains(profile.Requirements, r => r.CapabilityCode == cap && r.Priority == RequirementPriority.Must && r.Origin == RequirementOrigin.Stated);
    }

    [Theory]
    [InlineData("Our teams run Scrum and SAFe", "agile")]
    [InlineData("We use waterfall with stage-gate governance", "traditional")]
    [InlineData("PMO stage-gates for projects and agile sprints for software", "hybrid")]
    public void Heuristic_detects_methodology(string text, string expected) =>
        Assert.Equal(expected, HeuristicRequirementsExtractor.ExtractOrganization(text).Methodology);

    [Fact]
    public void Heuristic_parses_k_suffix_and_does_not_confuse_team_word_with_ms_teams()
    {
        var org = HeuristicRequirementsExtractor.ExtractOrganization("About 1.5k employees. Teams use Jira.");
        Assert.Equal(1500, org.Employees);
        Assert.Equal(["Jira"], org.ExistingSystems);
    }

    [Theory]
    [InlineData("We are a 400-person professional services firm", 400)]
    [InlineData("about 3,500 staff across Europe", 3500)]
    public void Heuristic_parses_employee_phrasings(string text, int expected) =>
        Assert.Equal(expected, HeuristicRequirementsExtractor.ExtractOrganization(text).Employees);

    [Fact]
    public void Analyzer_discards_llm_capability_codes_outside_the_taxonomy()
    {
        var ai = new RequirementProfile("planview", new OrganizationProfile(),
            [
                new Requirement("R1", "Portfolio", "portfolio-management", RequirementPriority.Must, RequirementOrigin.Stated),
                new Requirement("R2", "Quantum scheduling", "quantum-scheduler", RequirementPriority.Must, RequirementOrigin.Stated),
            ], [], "llm");

        var validated = RequirementsAnalyzer.Validate(ai, TestPack.Planview);

        Assert.Single(validated.Requirements);
        Assert.Contains("Quantum scheduling", validated.UnmappedStatements);
    }

    [Fact]
    public void Merge_prefers_deterministic_numbers_over_model_numbers()
    {
        var baseline = new RequirementProfile("planview", new OrganizationProfile { Employees = 2000 }, [], [], "heuristic");
        var ai = new RequirementProfile("planview", new OrganizationProfile { Employees = 20000, Industry = "Banking" }, [], [], "llm");

        var merged = RequirementsAnalyzer.Merge(baseline, ai, "llm+heuristic");

        Assert.Equal(2000, merged.Organization.Employees);
        Assert.Equal("Banking", merged.Organization.Industry);
    }

    [Fact]
    public async Task Analyzer_falls_back_to_heuristic_when_ai_extractor_throws()
    {
        var analyzer = new RequirementsAnalyzer([new ThrowingExtractor(), new HeuristicRequirementsExtractor()], NullLogger<RequirementsAnalyzer>.Instance);
        var profile = await analyzer.AnalyzeAsync(TestPack.ExampleScenario, TestPack.Planview, default);
        Assert.Equal("heuristic", profile.AnalyzerUsed);
        Assert.NotEmpty(profile.Requirements);
    }

    [Fact]
    public async Task Clarifications_ask_only_for_missing_facts()
    {
        var profile = (await new HeuristicRequirementsExtractor().ExtractAsync(TestPack.ExampleScenario, TestPack.Planview, default))!;
        var questions = new ClarificationService().GetQuestions(profile, TestPack.Planview, max: 20);

        Assert.DoesNotContain(questions, q => q.Code == "q-employees");
        Assert.DoesNotContain(questions, q => q.Code == "q-pms");
        Assert.Contains(questions, q => q.Code == "q-method");
        Assert.Contains(questions, q => q.Code == "q-bus");
    }

    private sealed class ThrowingExtractor : Avantra.AIAdvisory.Application.Abstractions.IRequirementsExtractor
    {
        public string Name => "llm";
        public Task<RequirementProfile?> ExtractAsync(string text, VendorPack pack, CancellationToken ct) => throw new HttpRequestException("model down");
    }
}
