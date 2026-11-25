using System.Text.RegularExpressions;

namespace Avantra.AIAdvisory.Application.Safety;

/// <summary>
/// Cheap, deterministic first-line defences. These do not replace platform controls (Azure AI Content Safety
/// Prompt Shields, network isolation, least privilege) but they catch the obvious cases offline and in tests.
/// </summary>
public static partial class ContentSafety
{
    [GeneratedRegex(@"(ignore|disregard|forget)\s+(all\s+|any\s+)?(previous|prior|above|earlier)\s+(instructions|prompts|rules)|you\s+are\s+now\s+(a|an|the)\b|system\s*prompt|<\s*/?\s*(system|assistant)\s*>|reveal\s+(your|the)\s+(instructions|prompt|api\s*key)|act\s+as\s+(an?\s+)?(unrestricted|jailbroken)", RegexOptions.IgnoreCase)]
    private static partial Regex InjectionPattern();

    [GeneratedRegex(@"(?:[$€£¥]\s?\d[\d,]*(?:\.\d+)?)|(?:\b\d[\d,]*(?:\.\d+)?\s?(?:USD|EUR|GBP|AUD|CAD|dollars|euros)\b)|(?:\bper\s+(?:user|seat|month|year)\s+(?:costs?|price)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex PricePattern();

    [GeneratedRegex(@"\b(?:\d{3}-\d{2}-\d{4}|\d{4}[ -]?\d{4}[ -]?\d{4}[ -]?\d{4})\b")]
    private static partial Regex SensitiveNumberPattern();

    public static bool LooksLikePromptInjection(string text, out string? match)
    {
        var m = InjectionPattern().Match(text ?? string.Empty);
        match = m.Success ? m.Value : null;
        return m.Success;
    }

    public static bool ContainsPriceStatement(string text) => PricePattern().IsMatch(text ?? string.Empty);

    /// <summary>Redacts card-number / national-id shaped values before text is sent to a model or logged.</summary>
    public static string RedactSensitive(string text) => SensitiveNumberPattern().Replace(text ?? string.Empty, "[REDACTED]");

    /// <summary>Wraps untrusted retrieved content so the model treats it as data, never instructions.</summary>
    public static string AsUntrustedData(string label, string content) =>
        $"<untrusted_source label=\"{label.Replace("\"", "'")}\">\n{content.Replace("</untrusted_source>", "</ untrusted_source>")}\n</untrusted_source>";
}
