using System.Windows;

namespace GetPdf;

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
