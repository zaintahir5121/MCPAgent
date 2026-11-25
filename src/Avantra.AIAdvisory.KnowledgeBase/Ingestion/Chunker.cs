using Avantra.AIAdvisory.KnowledgeBase.Parsing;

namespace Avantra.AIAdvisory.KnowledgeBase.Ingestion;

public sealed record ChunkingOptions(int TargetChars = 900, int OverlapChars = 150);

/// <summary>Section-aware chunker: never crosses a heading, prefers paragraph boundaries, overlaps for context.</summary>
public static class Chunker
{
    public static IEnumerable<(string? Section, string Text)> Chunk(ParsedDocument doc, ChunkingOptions? options = null)
    {
        options ??= new ChunkingOptions();
        foreach (var section in doc.Sections)
        {
            var paragraphs = section.Text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var current = "";
            foreach (var para in paragraphs.SelectMany(p => SplitLong(p, options.TargetChars)))
            {
                if (current.Length > 0 && current.Length + para.Length + 2 > options.TargetChars)
                {
                    yield return (section.Heading, current);
                    var tail = current.Length > options.OverlapChars ? current[^options.OverlapChars..] : current;
                    var space = tail.IndexOf(' ');
                    current = (space > 0 ? tail[(space + 1)..] : tail) + "\n\n" + para;
                }
                else current = current.Length == 0 ? para : current + "\n\n" + para;
            }
            if (current.Trim().Length > 0) yield return (section.Heading, current.Trim());
        }
    }

    private static IEnumerable<string> SplitLong(string text, int max)
    {
        if (text.Length <= max) { yield return text; yield break; }
        var start = 0;
        while (start < text.Length)
        {
            var len = Math.Min(max, text.Length - start);
            var end = start + len;
            if (end < text.Length)
            {
                var stop = text.LastIndexOfAny(['.', '!', '?', '\n'], end - 1, len);
                if (stop > start + max / 2) end = stop + 1;
            }
            yield return text[start..end].Trim();
            start = end;
        }
    }
}
