namespace GetPdf;

/// <summary>A bounded, highlighted text occurrence on a visible page (not a PDF drawing coordinate).</summary>
public sealed record PdfTextHit(int PageIndex, string Snippet, int MatchStart = 0, int MatchLength = 0)
{
    public string PageLabel => $"Стр. {PageIndex + 1}";
    public string Before => Snippet[..Math.Clamp(MatchStart, 0, Snippet.Length)];
    public string Matched => Snippet.Substring(Math.Clamp(MatchStart, 0, Snippet.Length),
        Math.Clamp(MatchLength, 0, Snippet.Length - Math.Clamp(MatchStart, 0, Snippet.Length)));
    public string After => Snippet[(Math.Clamp(MatchStart, 0, Snippet.Length) +
        Math.Clamp(MatchLength, 0, Snippet.Length - Math.Clamp(MatchStart, 0, Snippet.Length)))..];
}

public sealed class PdfTextIndex
{
    public const int MaxResults = 500;
    public const int MaxQueryLength = 256;
    private const int SnippetContext = 48;
    private readonly IReadOnlyList<string> _sourceTexts;

    public PdfTextIndex(IReadOnlyList<string> sourceTexts) =>
        _sourceTexts = sourceTexts ?? throw new ArgumentNullException(nameof(sourceTexts));

    /// <summary>Read-only source text for conservative PDF word-to-index verification.</summary>
    public string GetSourceText(int sourcePage) =>
        sourcePage >= 0 && sourcePage < _sourceTexts.Count
            ? _sourceTexts[sourcePage] ?? string.Empty : string.Empty;

    /// <summary>
    /// Finds every non-overlapping occurrence in visible page order. A duplicated page produces
    /// a separate result. The bounded list avoids creating thousands of WPF search items.
    /// Coordinates are intentionally not inferred from extracted text offsets.
    /// </summary>
    public IReadOnlyList<PdfTextHit> Find(IReadOnlyList<PdfPageReference> pages, string? query)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<PdfTextHit>();
        var needle = query.Trim();
        if (needle.Length > MaxQueryLength) return Array.Empty<PdfTextHit>();
        var results = new List<PdfTextHit>();
        for (int i = 0; i < pages.Count; i++)
        {
            int source = pages[i].SourcePage;
            if (source < 0 || source >= _sourceTexts.Count) continue;
            string content = _sourceTexts[source] ?? string.Empty;
            int offset = 0;
            while (offset < content.Length)
            {
                int start = content.IndexOf(needle, offset, StringComparison.OrdinalIgnoreCase);
                if (start < 0) break;
                int left = Math.Max(0, start - SnippetContext);
                int right = (int)Math.Min(content.Length, (long)start + needle.Length + SnippetContext);
                string snippet = content.Substring(left, right - left).Replace('\r', ' ').Replace('\n', ' ');
                results.Add(new PdfTextHit(i, snippet, start - left, needle.Length));
                if (results.Count >= MaxResults) return results;
                offset = start + needle.Length;
            }
        }
        return results;
    }
}
