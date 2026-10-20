using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Aspose.Pdf.Text;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using AsposeDocument = Aspose.Pdf.Document;

namespace GetPdf;

internal sealed record PdfEditableFragment(int Index, string Text, double X, double Y, double Width, double Height,
    string Font, double FontSize, string SourceHash);
internal sealed record PdfTextReplacement(PdfEditableFragment Fragment, string Text, string Font, double FontSize, string Color);
internal sealed record PdfTextEditResult(string Path, string Font, int PageCount);

/// <summary>Changes real text operators in a separate, verified PDF; never simulates an edit with a white rectangle.</summary>
internal static class PdfSourceTextEditor
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string Hash(string input)
    {
        using var stream = File.OpenRead(input);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static TextFragmentAbsorber Absorb(AsposeDocument doc, int page)
    {
        if (page < 0 || page >= doc.Pages.Count) throw new ArgumentOutOfRangeException(nameof(page));
        var absorber = new TextFragmentAbsorber(); absorber.Visit(doc.Pages[page + 1]); return absorber;
    }
    public static async Task<IReadOnlyList<PdfEditableFragment>> ReadFragmentsAsync(string input, int page, CancellationToken cancellation)
    {
        await Gate.WaitAsync(cancellation);
        try
        {
            return await Task.Run<IReadOnlyList<PdfEditableFragment>>(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                PdfStructureGuard.EnsureSafeToRewrite(input);
                using var doc = AsposeDocument.Open(input);
                var absorber = Absorb(doc, page);
                var box = doc.Pages[page + 1].Rect ?? throw new NotSupportedException("Неизвестная геометрия страницы.");
                string hash = Hash(input);
                return absorber.TextFragments.Select((f, i) => new PdfEditableFragment(i, f.Text,
                    (f.Rectangle!.LLX - box.LLX) / box.Width, (box.URY - f.Rectangle.URY) / box.Height,
                    f.Rectangle.Width / box.Width, f.Rectangle.Height / box.Height,
                    f.TextState.Font?.FontName ?? "", f.TextState.FontSize, hash))
                    .Where(f => !string.IsNullOrWhiteSpace(f.Text)).ToArray();
            }, cancellation);
        }
        finally { Gate.Release(); }
    }
    public static async Task<PdfTextEditResult> ReplaceAsync(string input, int page, PdfTextReplacement replacement,
        string output, CancellationToken cancellation)
    {
        if (Path.GetFullPath(input).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Сохраните изменения отдельной копией.");
        if (File.Exists(output)) throw new IOException("Файл результата уже существует.");
        if (replacement.Text.Length is 0 or > 2000 || replacement.Text.Any(char.IsControl))
            throw new ArgumentException("Редактирование поддерживает одну непустую строку до 2000 символов.");
        if (!double.IsFinite(replacement.FontSize) || replacement.FontSize is < 4 or > 96)
            throw new ArgumentOutOfRangeException(nameof(replacement.FontSize));
        if (!Regex.IsMatch(replacement.Color, "^#[0-9a-fA-F]{6}$")) throw new ArgumentException("Укажите цвет #RRGGBB.");
        var fonts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { ["Arial"] = "arial.ttf", ["Times New Roman"] = "times.ttf", ["Courier New"] = "cour.ttf" };
        if (!fonts.TryGetValue(replacement.Font, out var file)) throw new NotSupportedException("Выберите установленный шрифт из списка.");
        string fontPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), file);
        if (!File.Exists(fontPath)) throw new NotSupportedException("Выбранный шрифт не установлен в Windows.");
        await Gate.WaitAsync(cancellation);
        string temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, ".text-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            return await Task.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                if (Hash(input) != replacement.Fragment.SourceHash) throw new InvalidOperationException("Документ изменился. Выберите фрагмент заново.");
                PdfStructureGuard.EnsureSafeToRewrite(input);
                using var doc = AsposeDocument.Open(input);
                var absorber = Absorb(doc, page);
                var fragments = absorber.TextFragments.ToArray();
                if (replacement.Fragment.Index < 0 || replacement.Fragment.Index >= fragments.Length ||
                    fragments[replacement.Fragment.Index].Text != replacement.Fragment.Text)
                    throw new InvalidOperationException("Выбранный фрагмент устарел.");
                var target = fragments[replacement.Fragment.Index];
                var font = FontRepository.OpenFont(fontPath);
                double width = font.MeasureString(replacement.Text, replacement.FontSize);
                var box = doc.Pages[page + 1].Rect ?? throw new NotSupportedException("Неизвестная геометрия страницы.");
                double right = target.Rectangle!.LLX + width;
                var targetRect = target.Rectangle!;
                if (right > box.URX - 4 || targetRect.Height > 0 && fragments.Any(f => !ReferenceEquals(f, target) && f.Rectangle is { } r &&
                    Math.Abs(r.LLY - targetRect.LLY) < targetRect.Height * .5 &&
                    r.LLX > targetRect.LLX && r.LLX < right))
                    throw new InvalidOperationException("Новый текст выходит за границы или перекрывает соседний фрагмент. Уменьшите текст или размер шрифта.");
                string[] before = ReadTexts(input);
                // The user explicitly chose the replacement font; no invisible subset-font fallback.
                target.Text = replacement.Text;
                target.TextState.Font = font;
                target.TextState.FontSize = (float)replacement.FontSize;
                var rgb = System.Drawing.Color.FromArgb(Convert.ToInt32(replacement.Color[1..3], 16),
                    Convert.ToInt32(replacement.Color[3..5], 16), Convert.ToInt32(replacement.Color[5..7], 16));
                target.TextState.ForegroundColor = Aspose.Pdf.Color.FromRgb(rgb);
                cancellation.ThrowIfCancellationRequested();
                doc.Save(temp);
                PdfOutputVerifier.Verify(temp, doc.Pages.Count);
                string[] after = ReadTexts(temp);
                if (after.Length != before.Length || !after[page].Contains(Normalize(replacement.Text), StringComparison.Ordinal))
                    throw new InvalidDataException("Движок не сохранил новый текст корректно. Результат отклонён.");
                for (int i = 0; i < before.Length; i++)
                    if (i != page && before[i] != after[i]) throw new InvalidDataException("Изменился текст другой страницы. Результат отклонён.");
                if (replacement.Text != replacement.Fragment.Text &&
                    Count(after[page], Normalize(replacement.Fragment.Text)) != Count(before[page], Normalize(replacement.Fragment.Text)) - 1 +
                    Count(Normalize(replacement.Text), Normalize(replacement.Fragment.Text)))
                    throw new InvalidDataException("Движок изменил не только выбранный фрагмент. Результат отклонён.");
                cancellation.ThrowIfCancellationRequested();
                File.Move(temp, output);
                return new PdfTextEditResult(output, replacement.Font, after.Length);
            }, cancellation);
        }
        finally { Gate.Release(); if (File.Exists(temp)) File.Delete(temp); }
    }
    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();
    private static string[] ReadTexts(string path)
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
        return pdf.GetPages().Select(p => Normalize(ContentOrderTextExtractor.GetText(p))).ToArray();
    }
    private static int Count(string text, string value)
    {
        if (value.Length == 0) return 0;
        int count = 0, at = 0;
        while ((at = text.IndexOf(value, at, StringComparison.Ordinal)) >= 0) { count++; at += value.Length; }
        return count;
    }
}
