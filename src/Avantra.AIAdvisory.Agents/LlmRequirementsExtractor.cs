using System.Text.Json;
using System.Text.Json.Serialization;
using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Domain.Advisory;
using Avantra.AIAdvisory.Domain.Catalogue;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Avantra.AIAdvisory.Agents;

/// <summary>
/// LLM-based extraction constrained to the vendor's capability taxonomy. Output is parsed defensively and
/// re-validated by <c>RequirementsAnalyzer</c>; failure returns null so the deterministic extractor is used.
/// </summary>
public sealed class LlmRequirementsExtractor(IServiceProvider services, ILogger<LlmRequirementsExtractor> logger) : IRequirementsExtractor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public string Name => "llm";

    public async Task<RequirementProfile?> ExtractAsync(string text, VendorPack pack, CancellationToken ct)
    {
        var chat = services.GetService<IChatClient>();
        if (chat is null) return null;

        var taxonomy = string.Join("\n", pack.Capabilities.Select(c => $"- {c.Code}: {c.Name} ({string.Join(", ", c.Synonyms.Take(6))})"));
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, Prompts.RequirementsExtraction + "\n\nCapability codes:\n" + taxonomy),
            new(ChatRole.User, $"<customer_text>\n{text}\n</customer_text>"),
        };
        var response = await chat.GetResponseAsync(messages, new ChatOptions
        {
            Temperature = 0,
            ResponseFormat = ChatResponseFormat.Json,
            MaxOutputTokens = 1500,
        }, ct);

        var dto = ParseJson<ExtractionDto>(response.Text);
        if (dto is null)
        {
            logger.LogWarning("LLM extraction returned unparseable output.");
            return null;
        }

        var reqs = (dto.Requirements ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r.CapabilityCode))
            .Select((r, i) => new Requirement($"R{i + 1}", r.Statement ?? r.CapabilityCode!, r.CapabilityCode!.Trim().ToLowerInvariant(),
                Enum.TryParse<RequirementPriority>(r.Priority, true, out var p) ? p : RequirementPriority.Should,
                Enum.TryParse<RequirementOrigin>(r.Origin, true, out var o) ? o : RequirementOrigin.Stated))
            .ToList();
        var org = dto.Organization ?? new OrganizationProfile();
        org = org with { ExistingSystems = org.ExistingSystems ?? [], Regions = org.Regions ?? [] };
        return new RequirementProfile(pack.Vendor.Code, org, reqs, dto.Unmapped ?? [], Name);
    }

    /// <summary>Tolerates code fences and leading prose from OpenAI-compatible endpoints without strict JSON mode.</summary>
    internal static T? ParseJson<T>(string? text) where T : class
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonSerializer.Deserialize<T>(text[start..(end + 1)], Json); }
        catch (JsonException) { return null; }
    }

    internal sealed class ExtractionDto
    {
        public OrganizationProfile? Organization { get; set; }
        public List<RequirementDto>? Requirements { get; set; }
        public List<string>? Unmapped { get; set; }
    }

    internal sealed class RequirementDto
    {
        public string? Statement { get; set; }
        public string? CapabilityCode { get; set; }
        public string? Priority { get; set; }
        public string? Origin { get; set; }
    }
}
