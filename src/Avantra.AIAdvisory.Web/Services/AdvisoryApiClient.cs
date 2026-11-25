using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avantra.AIAdvisory.Domain.Advisory;
using Avantra.AIAdvisory.Domain.Catalogue;
using Avantra.AIAdvisory.Domain.Knowledge;

namespace Avantra.AIAdvisory.Web.Services;

public sealed record KnowledgeSearchResult(string Query, string VendorCode, IReadOnlyList<KnowledgeHit> Hits, string Notice);
public sealed record AssistantAnswer(string Answer, string Mode, IReadOnlyList<string> SourcesConsulted, string Notice);
public sealed record ServiceInfo(string Service, AiInfo Ai);
public sealed record AiInfo(bool Chat, bool Embeddings, string? Model, string Mode);

/// <summary>Typed client for the Advisory REST API (the same contract Nexa and other channels use).</summary>
public sealed class AdvisoryApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public Task<ServiceInfo?> InfoAsync(CancellationToken ct = default) => http.GetFromJsonAsync<ServiceInfo>("/", Json, ct);

    public async Task<IReadOnlyList<Vendor>> VendorsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<Vendor>>("api/v1/vendors", Json, ct) ?? [];

    public async Task<IReadOnlyList<Product>> ProductsAsync(string vendor, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<Product>>($"api/v1/vendors/{vendor}/products", Json, ct) ?? [];

    public async Task<IReadOnlyList<Capability>> CapabilitiesAsync(string vendor, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<Capability>>($"api/v1/vendors/{vendor}/capabilities", Json, ct) ?? [];

    public Task<AdvisoryResult> RecommendAsync(string vendor, string requirements, Guid? sessionId, CancellationToken ct = default) =>
        PostAsync<AdvisoryResult>("api/v1/advisory/recommend", new { vendorCode = vendor, requirements, sessionId }, ct);

    public Task<KnowledgeSearchResult> SearchAsync(string vendor, string query, CancellationToken ct = default) =>
        PostAsync<KnowledgeSearchResult>($"api/v1/vendors/{vendor}/knowledge/search", new { query, top = 8 }, ct);

    public Task<AssistantAnswer> AskAsync(string vendor, string question, CancellationToken ct = default) =>
        PostAsync<AssistantAnswer>("api/v1/advisory/ask", new { vendorCode = vendor, question }, ct);

    public async Task ReviewAsync(Guid sessionId, bool approve, string? comment, CancellationToken ct = default) =>
        (await http.PostAsJsonAsync($"api/v1/advisory/sessions/{sessionId}/review", new { approve, comment }, Json, ct)).EnsureSuccessStatusCode();

    public Task<string> ReportHtmlAsync(Guid sessionId, string? customer, CancellationToken ct = default) =>
        http.GetStringAsync($"api/v1/advisory/sessions/{sessionId}/report?format=html&customerName={Uri.EscapeDataString(customer ?? "")}", ct);

    public Task<string> ReportMarkdownAsync(Guid sessionId, string? customer, CancellationToken ct = default) =>
        http.GetStringAsync($"api/v1/advisory/sessions/{sessionId}/report?format=md&customerName={Uri.EscapeDataString(customer ?? "")}", ct);

    private async Task<T> PostAsync<T>(string url, object body, CancellationToken ct)
    {
        var res = await http.PostAsJsonAsync(url, body, Json, ct);
        if (!res.IsSuccessStatusCode)
        {
            var problem = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"API {(int)res.StatusCode}: {problem}");
        }
        return (await res.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }
}
