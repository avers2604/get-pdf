using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace GetPdf;

/// <summary>
/// Places a page number on a page drawn into <c>pageRect</c> with the geometry the PDF
/// writer uses (Arial 10 pt, 28 pt margin, A4 595 pt width as the scale reference), so
/// the viewer, printing and the saved copy show the number in the same spot.
/// </summary>
internal static class PdfPageNumberLayout
{
    private static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)));

    internal static (FormattedText Text, Point Origin) Place(string label,
        PdfPageNumberPosition position, Rect pageRect)
    {
        double unit = pageRect.Width / 595.0; // one PDF point on this page
        var text = new FormattedText(label, CultureInfo.GetCultureInfo("ru-RU"),
            FlowDirection.LeftToRight, new Typeface("Arial"), Math.Max(4, 10 * unit), Ink, 1.0);
        double margin = 28 * unit;
        double x = position == PdfPageNumberPosition.BottomCenter
            ? pageRect.Left + (pageRect.Width - text.Width) / 2
            : pageRect.Right - margin - text.Width;
        double y = position == PdfPageNumberPosition.TopRight
            ? pageRect.Top + margin - 12 * unit
            : pageRect.Bottom - margin - 4 * unit;
        return (text, new Point(x, y));
    }

    private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }
}

/// <summary>Viewer overlay: draws the page number over a page image of any size.</summary>
internal sealed class PdfPageNumberOverlay : FrameworkElement
{
    private string _label = "";
    private PdfPageNumberPosition _position;

    public PdfPageNumberOverlay() => IsHitTestVisible = false;

    public void Set(string label, PdfPageNumberPosition position)
    {
        _label = label ?? "";
        _position = position;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_label.Length == 0 || RenderSize.Width <= 0 || RenderSize.Height <= 0) return;
        var (text, origin) = PdfPageNumberLayout.Place(_label, _position, new Rect(RenderSize));
        drawingContext.DrawText(text, origin);
    }
}
