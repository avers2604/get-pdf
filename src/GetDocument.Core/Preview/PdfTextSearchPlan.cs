using System.Globalization;
using System.Text;

namespace GetDocument.Core.Preview;

/// <summary>
/// Pure, bounded page-text search. Works on text extracted from a PDF's text
/// layer; it does NOT OCR scans or calculate glyph highlights.
/// </summary>
public static class PdfTextSearchPlan
{
    public const int MaxQueryLength = 128;
    public const int MaxCharactersPerPage = 250_000;
    public const int MaxMatchesPerPage = 100;
    public const int MaxExcerptLength = 160;

    public static string NormalizeQuery(string? query)
    {
        ArgumentNullException.ThrowIfNull(query);
        string normalized = query.Trim();
        if (normalized.Length is < 1 or > MaxQueryLength ||
            normalized.Any(c => char.IsControl(c) ||
                CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format))
            throw new ArgumentException("Поисковая строка должна содержать от 1 до 128 символов без управляющих знаков.",
                nameof(query));
        return normalized;
    }

    public static PdfTextPageMatch? FindInPage(int pageIndex, string? pageText, string normalizedQuery)
    {
        if (pageIndex is < 0 or >= PdfPreviewPlan.MaxPages)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (string.IsNullOrWhiteSpace(normalizedQuery) ||
            normalizedQuery.Length > MaxQueryLength || normalizedQuery.Any(c =>
                char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format))
            throw new ArgumentException("Недопустимая строка поиска.", nameof(normalizedQuery));
        if (string.IsNullOrEmpty(pageText)) return null;

        int limit = Math.Min(pageText.Length, MaxCharactersPerPage);
        int count = 0;
        int first = -1;
        int from = 0;
        while (from <= limit - normalizedQuery.Length && count < MaxMatchesPerPage)
        {
            int at = pageText.IndexOf(normalizedQuery, from,
                limit - from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) break;
            if (first < 0) first = at;
            count++;
            from = at + Math.Max(1, normalizedQuery.Length);
        }

        if (count == 0) return null;

        int start = Math.Max(0, first - 36);
        int excerptLength = Math.Min(MaxExcerptLength,
            Math.Min(limit - start, normalizedQuery.Length + 112));
        var clean = new StringBuilder(excerptLength);
        for (int i = start; i < start + excerptLength; i++)
        {
            char ch = pageText[i];
            clean.Append(char.IsControl(ch) || char.IsWhiteSpace(ch) ||
                CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format ? ' ' : ch);
        }

        return new PdfTextPageMatch(pageIndex, count, clean.ToString().Trim(),
            pageText.Length > MaxCharactersPerPage || count == MaxMatchesPerPage);
    }
}

public sealed record PdfTextPageMatch(
    int PageIndex, int Occurrences, string Excerpt, bool IsPartial);
