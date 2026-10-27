using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GetPdf;

namespace GetDocument.Desktop.Pdf;

/// <summary>
/// One page of the continuous view: the PDFium raster (rotated as planned) plus overlays
/// drawn with the same geometry as GET PDF's writer — stamp (Arial 15 bold, dark red, 20 pt
/// inset), page number (Arial 10, #333, 28 pt margin), red rectangles, yellow notes — and the
/// search / text-selection highlights. All overlay coordinates are normalised (0..1).
/// </summary>
internal sealed class PdfPageView : Border
{
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly LayoutTransformControl _rotated = new();
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Canvas _highlights = new() { IsHitTestVisible = false };
    private readonly TextBlock _placeholder = new()
    {
        Text = "Страница загружается…",
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Color.Parse("#8A91A8"))
    };
    private Size _pagePoints;

    public int Index { get; set; }
    public PdfPageReference Reference { get; private set; } = new(0, 0);
    public int RenderedWidth { get; private set; }
    public bool HasImage => _image.Source is not null;

    public PdfPageView()
    {
        Background = Brushes.White;
        BorderBrush = new SolidColorBrush(Color.Parse("#D5DAE5"));
        BorderThickness = new Thickness(1);
        Margin = new Thickness(0, 0, 0, 16);
        HorizontalAlignment = HorizontalAlignment.Center;
        _rotated.Child = _image;
        var stack = new Grid();
        stack.Children.Add(_placeholder);
        stack.Children.Add(_rotated);
        stack.Children.Add(_highlights);
        stack.Children.Add(_overlay);
        Child = stack;
        ClipToBounds = true;
    }

    /// <summary>Displayed size in DIP for a display width; swaps axes for 90°/270°.</summary>
    public void Configure(int index, PdfPageReference reference, Size pagePoints, double displayWidth)
    {
        Index = index;
        bool sideways = reference.Rotation is 90 or 270;
        bool rotationChanged = Reference.Rotation != reference.Rotation || Reference.SourcePage != reference.SourcePage;
        Reference = reference;
        _pagePoints = pagePoints;
        double w = sideways ? pagePoints.Height : pagePoints.Width;
        double h = sideways ? pagePoints.Width : pagePoints.Height;
        Width = displayWidth;
        Height = displayWidth * h / w;
        _image.Width = sideways ? Height : Width;
        _image.Height = sideways ? Width : Height;
        _rotated.LayoutTransform = reference.Rotation == 0 ? null : new RotateTransform(reference.Rotation);
        if (rotationChanged) ClearImage();
        DrawOverlays();
    }

    public void SetImage(Bitmap bitmap, int renderedWidth)
    {
        var previous = _image.Source as IDisposable;
        _image.Source = bitmap;
        RenderedWidth = renderedWidth;
        _placeholder.IsVisible = false;
        if (!ReferenceEquals(previous, bitmap)) previous?.Dispose();
    }

    public void ClearImage()
    {
        (_image.Source as IDisposable)?.Dispose();
        _image.Source = null;
        RenderedWidth = 0;
        _placeholder.IsVisible = true;
    }

    /// <summary>Displayed page size in PDF points (rotation applied): the writer's coordinate space.</summary>
    private Size DisplayedPoints => Reference.Rotation is 90 or 270
        ? new Size(_pagePoints.Height, _pagePoints.Width) : _pagePoints;

    public void DrawOverlays()
    {
        _overlay.Children.Clear();
        if (Width is not > 0 || Height is not > 0) return;
        double unit = Width / Math.Max(1, DisplayedPoints.Width); // one PDF point in DIP
        if (!string.IsNullOrWhiteSpace(Reference.Stamp))
        {
            var stamp = new TextBlock
            {
                Text = Reference.Stamp,
                FontFamily = new FontFamily("Arial"),
                FontWeight = FontWeight.Bold,
                FontSize = Math.Max(5, 15 * unit),
                Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x00, 0x00)),
                MaxWidth = Math.Max(30, Width - 40 * unit)
            };
            Canvas.SetLeft(stamp, 20 * unit);
            Canvas.SetTop(stamp, 20 * unit);
            _overlay.Children.Add(stamp);
        }
        if (!string.IsNullOrEmpty(Reference.PageNumber))
        {
            var number = new TextBlock
            {
                Text = Reference.PageNumber,
                FontFamily = new FontFamily("Arial"),
                FontSize = Math.Max(4, 10 * unit),
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                Width = Math.Max(30, Width - 56 * unit),
                TextAlignment = Reference.NumberPosition == PdfPageNumberPosition.BottomCenter
                    ? TextAlignment.Center : TextAlignment.Right
            };
            Canvas.SetLeft(number, 28 * unit);
            Canvas.SetTop(number, Reference.NumberPosition == PdfPageNumberPosition.TopRight
                ? 16 * unit : Height - 32 * unit);
            _overlay.Children.Add(number);
        }
        foreach (var r in Reference.Rectangles ?? [])
        {
            var shape = new Rectangle { Stroke = Brushes.Red, StrokeThickness = Math.Max(1, 2 * unit),
                Width = r.Width * Width, Height = r.Height * Height };
            Canvas.SetLeft(shape, r.X * Width);
            Canvas.SetTop(shape, r.Y * Height);
            _overlay.Children.Add(shape);
        }
        foreach (var note in Reference.Notes ?? [])
        {
            var box = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#FFFFE0")),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0, 0)),
                BorderThickness = new Thickness(1),
                Width = .4 * Width, Height = .12 * Height,
                Padding = new Thickness(4 * unit, 3 * unit),
                Child = new TextBlock
                {
                    Text = note.Text, TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Arial"), FontSize = Math.Max(4, 9 * unit),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0, 0))
                }
            };
            Canvas.SetLeft(box, note.X * Width);
            Canvas.SetTop(box, note.Y * Height);
            _overlay.Children.Add(box);
        }
    }

    /// <summary>Normalised rectangles (search hits or selected glyphs) in a translucent colour.</summary>
    public void SetHighlights(IReadOnlyList<PdfSearchHighlightRect> rects, Color color)
    {
        _highlights.Children.Clear();
        foreach (var r in rects)
        {
            var box = new Rectangle { Fill = new SolidColorBrush(color), Width = r.Width * Width, Height = r.Height * Height };
            Canvas.SetLeft(box, r.X * Width);
            Canvas.SetTop(box, r.Y * Height);
            _highlights.Children.Add(box);
        }
    }

    /// <summary>Rubber band while drawing a rectangle (normalised), or nothing.</summary>
    public void SetDraft(Rect? normalised)
    {
        _highlights.Children.RemoveAll(_highlights.Children.OfType<Rectangle>().Where(r => r.Tag is "draft").ToList());
        if (normalised is not { } n) return;
        var band = new Rectangle
        {
            Tag = "draft", Stroke = Brushes.Red, StrokeThickness = 2, StrokeDashArray = [4, 3],
            Width = n.Width * Width, Height = n.Height * Height
        };
        Canvas.SetLeft(band, n.X * Width);
        Canvas.SetTop(band, n.Y * Height);
        _highlights.Children.Add(band);
    }
}
