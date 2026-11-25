namespace Avantra.AIAdvisory.Infrastructure.AI;

/// <summary>
/// OpenAI-compatible provider settings. Bind from configuration section "AI"; supply secrets via environment
/// variables (AI__ApiKey), user-secrets (development) or Azure Key Vault (production). Never commit keys.
/// </summary>
public sealed class AiProviderOptions
{
    public const string Section = "AI";

    /// <summary>"OpenAICompatible" (any OpenAI-protocol endpoint incl. Azure OpenAI v1 / Azure AI Foundry) or "None".</summary>
    public string Provider { get; set; } = "OpenAICompatible";
    /// <summary>e.g. https://my-resource.openai.azure.com/openai/v1/ or https://api.openai.com/v1</summary>
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    /// <summary>Chat model / deployment name.</summary>
    public string? ChatModel { get; set; }
    /// <summary>Optional embedding model / deployment name. When empty, retrieval is keyword-only (BM25).</summary>
    public string? EmbeddingModel { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
    public float Temperature { get; set; } = 0.1f;
    public int MaxOutputTokens { get; set; } = 1200;

    public bool ChatConfigured =>
        !string.Equals(Provider, "None", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ChatModel);

    public bool EmbeddingsConfigured => ChatConfigured && !string.IsNullOrWhiteSpace(EmbeddingModel);
}
