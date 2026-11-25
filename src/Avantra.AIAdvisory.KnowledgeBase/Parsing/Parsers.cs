using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;

namespace Avantra.AIAdvisory.KnowledgeBase.Parsing;

public sealed record ParsedSection(string? Heading, string Text);

public sealed record ParsedDocument(string? Title, IReadOnlyList<ParsedSection> Sections, IReadOnlyDictionary<string, string> Metadata);

public interface IDocumentParser
{
    bool CanParse(string fileName, string? contentType);
    Task<ParsedDocument> ParseAsync(Stream content, string fileName, CancellationToken ct);
}

/// <summary>Selects a parser by extension / content type.</summary>
public sealed class DocumentParserRegistry(IEnumerable<IDocumentParser> parsers)
{
    public IDocumentParser Resolve(string fileName, string? contentType) =>
        parsers.FirstOrDefault(p => p.CanParse(fileName, contentType))
        ?? throw new NotSupportedException($"No parser for '{fileName}' ({contentType}). Supported: .md, .txt, .html, .pdf, .docx");
}

/// <summary>Markdown with optional front matter (<c>key: value</c> lines between <c>---</c> fences).</summary>
public sealed partial class MarkdownParser : IDocumentParser
{
    [GeneratedRegex(@"^(#{1,3})\s+(.+)$")]
    private static partial Regex Heading();

    public bool CanParse(string fileName, string? contentType) =>
        fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase) || contentType == "text/markdown";

    public async Task<ParsedDocument> ParseAsync(Stream content, string fileName, CancellationToken ct)
    {
        using var reader = new StreamReader(content, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(ct);
        return Parse(text);
    }

    internal static ParsedDocument Parse(string text)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[0].Trim() == "---")
        {
            var end = lines.FindIndex(1, l => l.Trim() == "---");
            if (end > 0)
            {
                foreach (var l in lines.Skip(1).Take(end - 1))
                {
                    var idx = l.IndexOf(':');
                    if (idx > 0) meta[l[..idx].Trim()] = l[(idx + 1)..].Trim().Trim('"');
                }
                lines = lines.Skip(end + 1).ToList();
            }
        }

        string? title = meta.GetValueOrDefault("title");
        var sections = new List<ParsedSection>();
        string? heading = null;
        var buffer = new StringBuilder();
        void Flush()
        {
            var t = buffer.ToString().Trim();
            if (t.Length > 0) sections.Add(new ParsedSection(heading, t));
            buffer.Clear();
        }
        foreach (var line in lines)
        {
            var m = Heading().Match(line);
            if (m.Success)
            {
                Flush();
                heading = m.Groups[2].Value.Trim();
                if (m.Groups[1].Value.Length == 1 && title is null) title = heading;
                continue;
            }
            buffer.AppendLine(line);
        }
        Flush();
        return new ParsedDocument(title, sections, meta);
    }
}

public sealed class PlainTextParser : IDocumentParser
{
    public bool CanParse(string fileName, string? contentType) =>
        fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || contentType == "text/plain";

    public async Task<ParsedDocument> ParseAsync(Stream content, string fileName, CancellationToken ct)
    {
        using var reader = new StreamReader(content, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(ct);
        return new ParsedDocument(Path.GetFileNameWithoutExtension(fileName), [new ParsedSection(null, text.Trim())], new Dictionary<string, string>());
    }
}

/// <summary>Dependency-free HTML extractor: drops script/style/nav, splits on h1-h3, decodes entities.</summary>
public sealed partial class HtmlParser : IDocumentParser
{
    [GeneratedRegex(@"<(head|script|style|nav|footer|header|noscript|svg|title)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Noise();
    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Title();
    [GeneratedRegex(@"<h([1-3])[^>]*>(.*?)</h\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Heading();
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();
    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();
    [GeneratedRegex(@"\n\s*\n+")]
    private static partial Regex BlankLines();

    public bool CanParse(string fileName, string? contentType) =>
        fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) || contentType == "text/html";

    public async Task<ParsedDocument> ParseAsync(Stream content, string fileName, CancellationToken ct)
    {
        using var reader = new StreamReader(content, Encoding.UTF8);
        return Parse(await reader.ReadToEndAsync(ct));
    }

    internal static ParsedDocument Parse(string html)
    {
        var title = Title().Match(html) is { Success: true } t ? Clean(t.Groups[1].Value) : null;
        html = Noise().Replace(html, " ");
        html = Regex.Replace(html, @"<(br|/p|/li|/div|/tr)[^>]*>", "\n", RegexOptions.IgnoreCase);
        var sections = new List<ParsedSection>();
        var matches = Heading().Matches(html);
        var last = 0;
        string? heading = null;
        foreach (Match m in matches)
        {
            Add(sections, heading, html[last..m.Index]);
            heading = Clean(m.Groups[2].Value);
            title ??= heading;
            last = m.Index + m.Length;
        }
        Add(sections, heading, html[last..]);
        return new ParsedDocument(title, sections, new Dictionary<string, string>());
    }

    private static void Add(List<ParsedSection> sections, string? heading, string fragment)
    {
        var text = Clean(fragment);
        if (text.Length > 0) sections.Add(new ParsedSection(heading, text));
    }

    private static string Clean(string s)
    {
        s = WebUtility.HtmlDecode(Tag().Replace(s, " "));
        s = Spaces().Replace(s, " ");
        return BlankLines().Replace(s, "\n\n").Trim();
    }
}

/// <summary>PDF text extraction (PdfPig, pure .NET). One section per page so citations can point at pages.</summary>
public sealed class PdfParser : IDocumentParser
{
    public bool CanParse(string fileName, string? contentType) =>
        fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || contentType == "application/pdf";

    public async Task<ParsedDocument> ParseAsync(Stream content, string fileName, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        using var pdf = PdfDocument.Open(ms.ToArray());
        var sections = pdf.GetPages()
            .Select(p => new ParsedSection($"Page {p.Number}", p.Text.Trim()))
            .Where(s => s.Text.Length > 0)
            .ToList();
        var title = pdf.Information.Title is { Length: > 0 } t ? t : Path.GetFileNameWithoutExtension(fileName);
        return new ParsedDocument(title, sections, new Dictionary<string, string>());
    }
}

/// <summary>DOCX extraction (Open XML SDK). Heading styles start new sections.</summary>
public sealed class DocxParser : IDocumentParser
{
    public bool CanParse(string fileName, string? contentType) =>
        fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) || contentType == "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public async Task<ParsedDocument> ParseAsync(Stream content, string fileName, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        ms.Position = 0;
        using var doc = WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        var sections = new List<ParsedSection>();
        string? heading = null;
        var buffer = new StringBuilder();
        foreach (var p in body?.Descendants<Paragraph>() ?? [])
        {
            var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? string.Empty;
            var text = p.InnerText.Trim();
            if (text.Length == 0) continue;
            if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) || style == "Title")
            {
                if (buffer.Length > 0) sections.Add(new ParsedSection(heading, buffer.ToString().Trim()));
                buffer.Clear();
                heading = text;
                continue;
            }
            buffer.AppendLine(text);
        }
        if (buffer.Length > 0) sections.Add(new ParsedSection(heading, buffer.ToString().Trim()));
        var title = doc.PackageProperties.Title is { Length: > 0 } t ? t : sections.FirstOrDefault()?.Heading ?? Path.GetFileNameWithoutExtension(fileName);
        return new ParsedDocument(title, sections, new Dictionary<string, string>());
    }
}
