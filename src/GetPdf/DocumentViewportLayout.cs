using System.Windows;

namespace GetPdf;

internal enum PdfPageLayoutMode { Continuous, Single, Spread }

internal static class DocumentViewportLayout
{
    public static Rect[] Measure(IReadOnlyList<Size> sizes, double zoom, double gap)
    {
        if (!double.IsFinite(zoom) || zoom <= 0 || !double.IsFinite(gap) || gap < 0)
            throw new ArgumentOutOfRangeException(nameof(zoom));
        if (sizes.Any(s => s.IsEmpty || s.Width <= 0 || s.Height <= 0 || !double.IsFinite(s.Width + s.Height)))
            throw new ArgumentException("Неверный размер страницы.", nameof(sizes));
        double width = sizes.Count == 0 ? 0 : sizes.Max(s => s.Width) * zoom;
        double y = 0;
        return sizes.Select(s =>
        {
            var rect = new Rect((width - s.Width * zoom) / 2, y, s.Width * zoom, s.Height * zoom);
            y += rect.Height + gap;
            return rect;
        }).ToArray();
    }
    public static Rect[] MeasureSpread(IReadOnlyList<Size> sizes, double zoom, double gap)
    {
        if (!double.IsFinite(zoom) || zoom <= 0 || !double.IsFinite(gap) || gap < 0)
            throw new ArgumentOutOfRangeException(nameof(zoom));
        if (sizes.Any(s => s.IsEmpty || s.Width <= 0 || s.Height <= 0 ||
                           !double.IsFinite(s.Width + s.Height)))
            throw new ArgumentException("Неверный размер страницы.", nameof(sizes));
        if (sizes.Count == 0) return [];
        double columnWidth = sizes.Max(s => s.Width) * zoom;
        var result = new Rect[sizes.Count];
        double y = 0;
        for (int i = 0; i < sizes.Count; i += 2)
        {
            int count = Math.Min(2, sizes.Count - i);
            double rowHeight = Enumerable.Range(i, count).Max(j => sizes[j].Height * zoom);
            for (int j = 0; j < count; j++)
            {
                Size size = sizes[i + j];
                double w = size.Width * zoom, h = size.Height * zoom;
                result[i + j] = new Rect(j * (columnWidth + gap) + (columnWidth - w) / 2,
                    y, w, h);
            }
            y += rowHeight + gap;
        }
        return result;
    }

    public static int[] VisibleRange(IReadOnlyList<Rect> pages, Rect viewport, int overscan)
    {
        if (pages.Count == 0) return [];
        int first = -1, last = -1;
        for (int i = 0; i < pages.Count; i++)
            if (pages[i].Bottom >= viewport.Top && pages[i].Top <= viewport.Bottom)
            { if (first < 0) first = i; last = i; }
        if (first < 0) return [];
        int start = Math.Max(0, first - Math.Max(0, overscan));
        int end = Math.Min(pages.Count - 1, last + Math.Max(0, overscan));
        return Enumerable.Range(start, end - start + 1).ToArray();
    }
}
