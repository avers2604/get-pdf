using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GetPdf;

public partial class PdfViewerWindow
{
    // Keep stored legacy tool preferences stable while presenting the agreed tab order.
    private int ActiveToolIndex
    {
        get => PdfToolModeBox.SelectedIndex switch { 1 => 4, 3 => 1, 4 => 3, var value => value };
        set => PdfToolModeBox.SelectedIndex = value switch { 4 => 1, 1 => 3, 3 => 4, _ => value };
    }
    private ScrollViewer PdfPreviewScroll => DocumentView.Scroll;
    private Size[] _sourcePageSizes = [];
    private string? _continuousFingerprint;
    private long _continuousRevision;
    private int _displayedPageIndex = -1;
    private string? _displayedFingerprint;
    private void InitializeContinuousView()
    {
        ActivePageParking.Children.Remove(PageVisual);
        DocumentView.ActivePageContent = PageVisual;
        DocumentView.DecoratePage = DecorateContinuousPage;
        DocumentView.CurrentPageChanged += index =>
        {
            if (_plan is null || index >= _plan.Pages.Count || _closing) return;
            if (_textSelectionMode)
                ClearTextSelection(resetMode: false);
            _pageIndex = index;
            _refreshingList = true;
            try { PageList.SelectedIndex = index; }
            finally { _refreshingList = false; }
            PagePosition.Text = $"из {_plan.Pages.Count}";
            if (!PageNumberBox.IsKeyboardFocusWithin)
                PageNumberBox.Text = (index + 1).ToString();
            SetControls();
            if (ActiveToolIndex == 4) _ = LoadTextFragmentsAsync();
        };
        DocumentView.PageRendered += async (index, bitmap) =>
        {
            if (_plan is null || index != _pageIndex || index >= _plan.Pages.Count || _closing) return;
            var reference = _plan.Pages[index];
            PdfPageImage.Source = bitmap;
            _displayedPageIndex = index; _displayedFingerprint = _continuousFingerprint;
            PdfPageImage.Width = bitmap.Width; PdfPageImage.Height = bitmap.Height;
            PageVisual.Width = bitmap.Width; PageVisual.Height = bitmap.Height;
            PageVisual.LayoutTransform = Transform.Identity;
            PdfSearchHighlightCanvas.Width = bitmap.Width; PdfSearchHighlightCanvas.Height = bitmap.Height;
            PdfTextSelectionCanvas.Width = bitmap.Width; PdfTextSelectionCanvas.Height = bitmap.Height;
            ShowRectangles(reference);
            if (_textSelectionMode) await EnsureSelectionWordsAsync();
            StampPreview.Text = reference.Stamp;
            StampPreview.Visibility = string.IsNullOrWhiteSpace(reference.Stamp) ? Visibility.Collapsed : Visibility.Visible;
            EmptyMessage.Visibility = Visibility.Collapsed;
            await RefreshPageSearchHighlightAsync(index, reference, bitmap.Width, bitmap.Height);
        };
    }
    private FrameworkElement DecorateContinuousPage(int index, BitmapSource bitmap)
    {
        var grid = new Grid { Width = bitmap.Width, Height = bitmap.Height };
        grid.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Fill });
        if (_plan is null || index >= _plan.Pages.Count) return grid;
        var page = _plan.Pages[index];
        var canvas = new Canvas { IsHitTestVisible = false, Width = bitmap.Width, Height = bitmap.Height };
        foreach (var rect in page.Rectangles ?? [])
        {
            var shape = new System.Windows.Shapes.Rectangle { Stroke = Brushes.Red, StrokeThickness = 2,
                Width = rect.Width * bitmap.Width, Height = rect.Height * bitmap.Height };
            Canvas.SetLeft(shape, rect.X * bitmap.Width); Canvas.SetTop(shape, rect.Y * bitmap.Height); canvas.Children.Add(shape);
        }
        foreach (var note in page.Notes ?? [])
        {
            var box = new Border { Width = .4 * bitmap.Width, Height = .12 * bitmap.Height, Background = Brushes.LightYellow,
                BorderBrush = Brushes.DarkRed, BorderThickness = new(1), Padding = new(4),
                Child = new TextBlock { Text = note.Text, Foreground = Brushes.DarkRed, FontSize = 11, TextWrapping = TextWrapping.Wrap } };
            Canvas.SetLeft(box, note.X * bitmap.Width); Canvas.SetTop(box, note.Y * bitmap.Height); canvas.Children.Add(box);
        }
        foreach (var draft in _nativeDrafts.ForPage(index))
        {
            var shape = new System.Windows.Shapes.Rectangle { Stroke = Brushes.Goldenrod, StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 3, 2 }, Width = (draft.Right - draft.Left) * bitmap.Width,
                Height = (draft.Bottom - draft.Top) * bitmap.Height };
            Canvas.SetLeft(shape, draft.Left * bitmap.Width); Canvas.SetTop(shape, draft.Top * bitmap.Height); canvas.Children.Add(shape);
        }
        grid.Children.Add(canvas);
        if (page.Stamp.Length > 0) grid.Children.Add(new TextBlock { Text = page.Stamp, Margin = new(27), FontSize = 18,
            FontWeight = FontWeights.Bold, Foreground = Brushes.DarkRed, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false });
        return grid;
    }
    private void ReadContinuousPageSizes()
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(_sourcePath!);
        _sourcePageSizes = pdf.GetPages().Select(page => new Size(page.Width * 96 / 72, page.Height * 96 / 72)).ToArray();
    }
    private Size CurrentContinuousSize()
    {
        if (_plan is null) return new(794, 1123);
        var page = _plan.Pages[_pageIndex];
        var size = _sourcePageSizes[page.SourcePage];
        return page.Rotation % 180 == 0 ? size : new(size.Height, size.Width);
    }
    private void ConfigureContinuousDocument()
    {
        if (_plan is null || _pageStore is null) return;
        string fingerprint = _sourcePath + ":" + CurrentPlanFingerprint();
        if (_continuousFingerprint == fingerprint) return;
        _continuousFingerprint = fingerprint;
        var references = _plan.Pages.ToArray();
        var store = _pageStore;
        var sizes = references.Select(p =>
        {
            var size = _sourcePageSizes[p.SourcePage];
            return p.Rotation % 180 == 0 ? size : new Size(size.Height, size.Width);
        }).ToArray();
        DocumentView.SetDocument(sizes, async (index, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            var reference = references[index];
            var image = await store.GetPageAsync(reference.SourcePage, cancellation);
            cancellation.ThrowIfCancellationRequested();
            BitmapSource bitmap = PdfPreviewRenderer.RenderBounded(image);
            if (reference.Rotation != 0) bitmap = new TransformedBitmap(bitmap, new RotateTransform(reference.Rotation));
            bitmap.Freeze();
            return bitmap;
        }, ++_continuousRevision);
    }
}
