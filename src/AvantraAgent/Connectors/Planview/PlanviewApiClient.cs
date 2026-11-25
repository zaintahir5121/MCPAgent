namespace AvantraAgent.Connectors.Planview;

public sealed class PlanviewApiOptions
{
    public const string Section = "Connectors:Planview:Api";
    /// <summary>Base URL of the Planview API you are licensed to call (e.g. your Planview Portfolios or AdaptiveWork tenant API).</summary>
    public string? BaseUrl { get; set; }
    /// <summary>API token. Put it in user-secrets / environment / Key Vault, never in appsettings.json.</summary>
    public string? ApiKey { get; set; }
    public string AuthHeader { get; set; } = "Authorization";
    public string AuthScheme { get; set; } = "Bearer";
}

/// <summary>
/// Agentic live-data access to Planview: read-only GET calls under the configured base URL.
/// Not configured → tools say so instead of guessing.
/// </summary>
public sealed class PlanviewApiClient(HttpClient http, Microsoft.Extensions.Options.IOptions<PlanviewApiOptions> options)
{
    private readonly PlanviewApiOptions _o = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_o.BaseUrl);

    public async Task<string> GetAsync(string relativePath, CancellationToken ct)
    {
        if (!IsConfigured) return "Planview API is not configured (Connectors:Planview:Api:BaseUrl). No live Planview data is available.";
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains("://") || relativePath.StartsWith("//") || relativePath.Contains(".."))
            return "Rejected: provide a relative API path under the configured Planview base URL.";

        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(_o.BaseUrl!.TrimEnd('/') + "/"), relativePath.TrimStart('/')));
        if (!string.IsNullOrWhiteSpace(_o.ApiKey))
            req.Headers.TryAddWithoutValidation(_o.AuthHeader, string.IsNullOrWhiteSpace(_o.AuthScheme) ? _o.ApiKey : $"{_o.AuthScheme} {_o.ApiKey}");
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (body.Length > 20_000) body = body[..20_000] + "…(truncated)";
        return res.IsSuccessStatusCode ? body : $"Planview API returned {(int)res.StatusCode}: {body[..Math.Min(500, body.Length)]}";
    }
}
