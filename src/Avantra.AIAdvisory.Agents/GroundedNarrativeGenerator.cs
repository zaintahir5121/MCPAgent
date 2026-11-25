using Avantra.AIAdvisory.Application.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Avantra.AIAdvisory.Agents;

/// <summary>Writes the executive summary from a fact sheet only. Its output is validated by NarrativeGuard.</summary>
public sealed class GroundedNarrativeGenerator(IServiceProvider services) : INarrativeGenerator
{
    public async Task<string?> GenerateExecutiveSummaryAsync(string factSheetJson, CancellationToken ct)
    {
        var chat = services.GetService<IChatClient>();
        if (chat is null) return null;
        var response = await chat.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, Prompts.ExecutiveSummary),
                new ChatMessage(ChatRole.User, $"<fact_sheet>\n{factSheetJson}\n</fact_sheet>"),
            ],
            new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 600 }, ct);
        return response.Text;
    }
}
