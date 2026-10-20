using UglyToad.PdfPig;

namespace GetPdf;

/// <summary>Read-only normalized word geometry, with no simulated character positions.</summary>
internal sealed record PdfSelectableWord(string Text, PdfSearchHighlightRect Bounds);

internal sealed class PdfWordSelection
{
    internal const int MaxWords = 2500;
    internal const int MaxCopiedChars = 50000;
    internal IReadOnlyList<PdfSelectableWord> Words { get; }

    internal PdfWordSelection(IReadOnlyList<PdfSelectableWord> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (words.Count > MaxWords)
            throw new ArgumentException("Превышен предел слов на странице.");
        foreach (var word in words)
        {
            if (word is null || string.IsNullOrWhiteSpace(word.Text) ||
                word.Text.Length > 512 || word.Text.Any(char.IsControl) ||
                word.Bounds is null)
                throw new ArgumentException("Повреждённое слово текстового слоя.");
            var b = word.Bounds;
            if (!double.IsFinite(b.X) || !double.IsFinite(b.Y) ||
                !double.IsFinite(b.Width) || !double.IsFinite(b.Height) ||
                b.X < 0 || b.Y < 0 || b.Width <= 0 || b.Height <= 0 ||
                b.X + b.Width > 1.00001 || b.Y + b.Height > 1.00001)
                throw new ArgumentException("Некорректная геометрия текстового слоя.");
        }
        Words = words.ToArray();
    }

    internal int HitTest(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return -1;
        for (int i = 0; i < Words.Count; i++)
        {
            var b = Words[i].Bounds;
            if (x >= b.X - .003 && x <= b.X + b.Width + .003 &&
                y >= b.Y - .007 && y <= b.Y + b.Height + .007)
                return i;
        }
        return -1;
    }

    internal string Extract(int anchor, int end)
    {
        if (Words.Count == 0 || anchor < 0 || end < 0 ||
            anchor >= Words.Count || end >= Words.Count) return "";
        int start = Math.Min(anchor, end), stop = Math.Max(anchor, end);
        var result = new System.Text.StringBuilder();
        PdfSelectableWord? previous = null;
        for (int i = start; i <= stop; i++)
        {
            var current = Words[i];
            if (result.Length + current.Text.Length + 1 > MaxCopiedChars)
                return "";
            if (previous is not null)
            {
                bool nextLine = Math.Abs(current.Bounds.Y - previous.Bounds.Y) >
                    Math.Max(.035, previous.Bounds.Height * 1.5);
                result.Append(nextLine ? '\n' : ' ');
            }
            result.Append(current.Text);
            previous = current;
        }
        return result.ToString();
    }
}

/// <summary>
/// Read only page words from PdfPig, conservatively limited to simple PDF
/// coordinates. Never guesses bounding boxes of scanned or rotated pages.
/// </summary>
internal static class PdfTextSelectionReader
{
    internal static PdfWordSelection Read(string source, int sourcePage, int rotation,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (rotation is not (0 or 90 or 180 or 270))
            return new PdfWordSelection([]);
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(source);
        if (sourcePage < 0 || sourcePage >= pdf.NumberOfPages)
            return new PdfWordSelection([]);
        var page = pdf.GetPage(sourcePage + 1);
        var crop = page.CropBox.Bounds;
        double left = Convert.ToDouble(crop.Left), bottom = Convert.ToDouble(crop.Bottom),
            width = Convert.ToDouble(crop.Width), height = Convert.ToDouble(crop.Height);
        if (page.Rotation.Value != 0 || width <= 0 || height <= 0 ||
            Math.Abs(left) > .001 || Math.Abs(bottom) > .001 ||
            Math.Abs(Convert.ToDouble(page.Width) - width) > .001 ||
            Math.Abs(Convert.ToDouble(page.Height) - height) > .001)
            return new PdfWordSelection([]);
        var rawWords = page.GetWords().Take(PdfWordSelection.MaxWords + 1).ToArray();
        if (rawWords.Length > PdfWordSelection.MaxWords)
            return new PdfWordSelection([]);
        var words = new List<PdfSelectableWord>();
        foreach (var word in rawWords)
        {
            cancellation.ThrowIfCancellationRequested();
            if (word.TextOrientation != UglyToad.PdfPig.Content.TextOrientation.Horizontal)
                return new PdfWordSelection([]);
            if (string.IsNullOrWhiteSpace(word.Text) || word.Text.Length > 512 ||
                word.Text.Any(char.IsControl))
                continue;
            var box = word.BoundingBox;
            var bounds = PdfSearchHighlightGeometry.FromPdfBox(
                Convert.ToDouble(box.Left), Convert.ToDouble(box.Bottom),
                Convert.ToDouble(box.Width), Convert.ToDouble(box.Height),
                left, bottom, width, height);
            if (bounds is null) return new PdfWordSelection([]);
            words.Add(new PdfSelectableWord(word.Text, bounds.Rotate(rotation)));
        }
        return new PdfWordSelection(words);
    }
}
