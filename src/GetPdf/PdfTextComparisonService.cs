using System.Security.Cryptography;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace GetPdf;

/// <summary>
/// Read-only alignment of PDF pages by extracted text. Empty text means image-only or
/// otherwise non-extractable pages, so equal empties do NOT prove visual equality.
/// </summary>
internal static class PdfTextComparisonService
{
    internal const int MaxPages = 1000;
    internal const int MaxTextPerPage = 2_000_000;
    internal enum Kind { EqualText, ChangedText, Added, Removed, UnverifiedImage }
    internal sealed record Change(Kind Type, int? LeftPage, int? RightPage);
    internal sealed record Report(IReadOnlyList<Change> Changes, int LeftPages, int RightPages)
    {
        public string ToRussianSummary()
        {
            var output = new StringBuilder();
            output.AppendLine("Сравнение текстового слоя PDF (не визуальное сравнение)");
            output.AppendLine($"Страницы: первый PDF — {LeftPages}; второй — {RightPages}.");
            foreach (var kind in new[] { Kind.EqualText, Kind.ChangedText, Kind.Added, Kind.Removed, Kind.UnverifiedImage })
                output.AppendLine($"{Name(kind)}: {Changes.Count(c => c.Type == kind)}");
            output.AppendLine("Первые отличия (не более 50):");
            foreach (var item in Changes.Where(c => c.Type is not Kind.EqualText).Take(50))
                output.AppendLine($"— {Name(item.Type)}: первый " +
                    (item.LeftPage is int l ? (l + 1).ToString() : "—") +
                    "; второй " + (item.RightPage is int n ? (n + 1).ToString() : "—"));
            output.Append("Важно: изображения, подписи, оформление и геометрия страниц не сравниваются. " +
                "Пустой текстовый слой нельзя считать доказательством одинакового содержимого.");
            return output.ToString();
        }
        private static string Name(Kind kind) => kind switch
        {
            Kind.EqualText => "Совпадение текста",
            Kind.ChangedText => "Изменённый текст",
            Kind.Added => "Добавленная страница",
            Kind.Removed => "Удалённая страница",
            _ => "Изображение/текст недоступен"
        };
    }

    internal static Report CompareFiles(string first, string second, CancellationToken token = default)
    {
        var a = ReadText(first, token);
        var b = ReadText(second, token);
        return Compare(a, b, token);
    }

    private static IReadOnlyList<string> ReadText(string path, CancellationToken token)
    {
        using var document = PdfDocument.Open(path);
        if (document.NumberOfPages > MaxPages)
            throw new InvalidDataException("Слишком много страниц для сравнения.");
        var pages = new List<string>(document.NumberOfPages);
        foreach (var page in document.GetPages())
        {
            token.ThrowIfCancellationRequested();
            var text = ContentOrderTextExtractor.GetText(page) ?? string.Empty;
            if (text.Length > MaxTextPerPage)
                throw new InvalidDataException("Слишком длинный текстовый слой страницы.");
            pages.Add(text);
        }
        return pages;
    }

    private static string NormalizedHash(string text)
    {
        if (text.Length > MaxTextPerPage)
            throw new InvalidDataException("Превышен лимит текста для сравнения.");
        var normalized = new StringBuilder(text.Length);
        bool white = false;
        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!white) normalized.Append(' ');
                white = true;
            }
            else { normalized.Append(ch); white = false; }
        }
        string value = normalized.ToString().Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    internal static Report Compare(IReadOnlyList<string> a, IReadOnlyList<string> b,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count > MaxPages || b.Count > MaxPages)
            throw new InvalidDataException("Слишком много страниц для сравнения.");
        var ah = a.Select(t => NormalizedHash(t ?? "")).ToArray();
        var bh = b.Select(t => NormalizedHash(t ?? "")).ToArray();
        var lcs = new int[a.Count + 1, b.Count + 1];
        for (int i = a.Count - 1; i >= 0; i--)
        {
            token.ThrowIfCancellationRequested();
            for (int j = b.Count - 1; j >= 0; j--)
                lcs[i, j] = ah[i] == bh[j] ? lcs[i + 1, j + 1] + 1 :
                    Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        }
        var changes = new List<Change>();
        int p = 0, q = 0;
        var removed = new List<int>();
        var added = new List<int>();
        void FlushDifferences()
        {
            int sameIndex = Math.Min(removed.Count, added.Count);
            for (int k = 0; k < sameIndex; k++)
                changes.Add(new Change(Kind.ChangedText, removed[k], added[k]));
            for (int k = sameIndex; k < removed.Count; k++)
                changes.Add(new Change(Kind.Removed, removed[k], null));
            for (int k = sameIndex; k < added.Count; k++)
                changes.Add(new Change(Kind.Added, null, added[k]));
            removed.Clear(); added.Clear();
        }
        while (p < a.Count || q < b.Count)
        {
            token.ThrowIfCancellationRequested();
            if (p < a.Count && q < b.Count && ah[p] == bh[q])
            {
                FlushDifferences();
                changes.Add(new Change(string.IsNullOrWhiteSpace(a[p]) || string.IsNullOrWhiteSpace(b[q])
                    ? Kind.UnverifiedImage : Kind.EqualText, p, q));
                p++; q++;
            }
            else if (q >= b.Count || p < a.Count && lcs[p + 1, q] >= lcs[p, q + 1])
                removed.Add(p++);
            else added.Add(q++);
        }
        FlushDifferences();
        return new Report(changes, a.Count, b.Count);
    }
}