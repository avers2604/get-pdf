using System.Windows;

namespace GetPdf;

internal readonly record struct PdfWindowBounds(
    double Left, double Top, double Width, double Height,
    double MinWidth, double MinHeight);

internal static class DocumentWindowLayout
{
    // The OS work area is already expressed in WPF device-independent pixels.
    // Reconsider window minimums on a 125%/150%/200% scaled desktop: a hard
    // 950-DIP minimum can exceed the entire work area.
    internal static PdfWindowBounds Calculate(
        Rect area, double left, double top, double width, double height,
        double minWidth, double minHeight)
    {
        if (!double.IsFinite(area.X) || !double.IsFinite(area.Y) ||
            !double.IsFinite(area.Width) || !double.IsFinite(area.Height) ||
            area.Width < 1 || area.Height < 1)
            throw new ArgumentOutOfRangeException(nameof(area));

        double minimumW = Math.Min(
            double.IsFinite(minWidth) && minWidth > 0 ? minWidth : 1, area.Width);
        double minimumH = Math.Min(
            double.IsFinite(minHeight) && minHeight > 0 ? minHeight : 1, area.Height);
        double desiredW = double.IsFinite(width) && width > 0 ? width : minimumW;
        double desiredH = double.IsFinite(height) && height > 0 ? height : minimumH;
        double w = Math.Clamp(desiredW, minimumW, area.Width);
        double h = Math.Clamp(desiredH, minimumH, area.Height);
        double x = double.IsFinite(left)
            ? Math.Clamp(left, area.Left, area.Right - w)
            : area.Left + (area.Width - w) / 2;
        double y = double.IsFinite(top)
            ? Math.Clamp(top, area.Top, area.Bottom - h)
            : area.Top + (area.Height - h) / 2;
        return new PdfWindowBounds(x, y, w, h, minimumW, minimumH);
    }

    internal static void Clamp(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var area = SystemParameters.WorkArea;
        var bounds = Calculate(area, window.Left, window.Top,
            window.Width, window.Height, window.MinWidth, window.MinHeight);
        // Apply lowered minimums FIRST or WPF can re-expand the window
        // after we reduce Width/Height on a small work area.
        window.MinWidth = bounds.MinWidth;
        window.MinHeight = bounds.MinHeight;
        window.Width = bounds.Width;
        window.Height = bounds.Height;
        window.Left = bounds.Left;
        window.Top = bounds.Top;
    }
}
