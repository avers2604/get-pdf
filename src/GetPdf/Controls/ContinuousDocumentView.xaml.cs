using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GetPdf.Controls;

/// <summary>Pixel scrolling over lightweight page bounds; only viewport neighbors have image visuals.</summary>
public partial class ContinuousDocumentView : UserControl
{
    // GET DS 4.1: the page sits as a raised sheet over the workspace underlay.
    private static readonly Brush PageEdge = Frozen(new SolidColorBrush(Color.FromRgb(0xE3, 0xE6, 0xEE)));
    private static readonly System.Windows.Media.Effects.DropShadowEffect PageShadow = Frozen(
        new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Color.FromRgb(0x24, 0x30, 0x5E), BlurRadius = 18, ShadowDepth = 3,
            Direction = 270, Opacity = 0.14, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
        });
    private static T Frozen<T>(T value) where T : Freezable { value.Freeze(); return value; }
    private IReadOnlyList<Size> _sizes = [];
    private Rect[] _bounds = [];
    private readonly DocumentRenderScheduler _renderer = new();
    private Func<int, CancellationToken, Task<BitmapSource>>? _render;
    private readonly Dictionary<int, Border> _realized = [];
    private CancellationTokenSource _cancel = new();
    private Task _worker = Task.CompletedTask;
    private long _revision;
    private int _current;
    private bool _queued;
    private bool _needsRender;
    private double _zoom = .9;
    private PdfPageLayoutMode _mode;
    public event Action<int>? CurrentPageChanged;
    public event Action<int, BitmapSource>? PageRendered;
    public ScrollViewer Scroll => Scroller;
    internal PdfPageLayoutMode LayoutMode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            DetachActive();
            foreach (var border in _realized.Values) PageCanvas.Children.Remove(border);
            _realized.Clear();
            _renderer.Invalidate();
            LayoutPages();
            ScrollToPage(_current);
        }
    }
    public FrameworkElement? ActivePageContent { get; set; }
    public Func<int, BitmapSource, FrameworkElement>? DecoratePage { get; set; }
    public int CurrentPage => _current;
    public double RelativePageOffset => _bounds.Length > _current
        ? (Scroller.VerticalOffset - _bounds[_current].Top - 24) / _bounds[_current].Height : 0;
    public void RestoreViewport(int index, double offset, double zoom)
    {
        Zoom = zoom; ScrollToPage(index);
        if (index >= 0 && index < _bounds.Length)
            Scroller.ScrollToVerticalOffset(_bounds[index].Top + 24 + offset * _bounds[index].Height);
    }
    public double Zoom
    {
        get => _zoom;
        set
        {
            double zoom = Math.Clamp(value, .15, 4);
            if (Math.Abs(zoom - _zoom) < .0001) return;
            var anchor = _bounds.Length > _current ? _bounds[_current] : Rect.Empty;
            double offset = anchor.IsEmpty ? 0 : (Scroller.VerticalOffset - anchor.Top - 24) / anchor.Height;
            _zoom = zoom; LayoutPages();
            if (_bounds.Length > _current) Scroller.ScrollToVerticalOffset(_bounds[_current].Top + 24 + offset * _bounds[_current].Height);
        }
    }
    public ContinuousDocumentView() { InitializeComponent(); }
    public void SetDocument(IReadOnlyList<Size> sizes, Func<int, CancellationToken, Task<BitmapSource>> render, long revision)
    {
        _cancel.Cancel(); _cancel.Dispose(); _cancel = new();
        _renderer.Invalidate();
        _revision = revision; _sizes = sizes; _render = render;
        _current = Math.Clamp(_current, 0, Math.Max(0, sizes.Count - 1));
        DetachActive(); _realized.Clear(); PageCanvas.Children.Clear(); LayoutPages();
    }
    public async Task StopAsync()
    {
        _cancel.Cancel(); _render = null; _renderer.Invalidate();
        try { await _worker; } catch (OperationCanceledException) { }
        DetachActive(); _realized.Clear(); PageCanvas.Children.Clear();
        _sizes = []; _bounds = []; PageCanvas.Height = 0;
    }
    public void ScrollToPage(int index)
    {
        if (index < 0 || index >= _bounds.Length) return;
        SelectPage(index);
        if (_mode == PdfPageLayoutMode.Single) LayoutPages();
        Scroller.ScrollToVerticalOffset(_bounds[index].Top + 24);
        QueueRender();
    }
    public void FitWidth()
    {
        if (_sizes.Count > 0 && Scroller.ViewportWidth > 80)
            Zoom = (Scroller.ViewportWidth - 64) / (_mode == PdfPageLayoutMode.Spread
                ? _sizes.Max(s => s.Width) * 2 + 24 : _sizes[_current].Width);
    }
    public void UpdatePageSize(int index, Size size)
    {
        if (index < 0 || index >= _sizes.Count || _sizes[index] == size) return;
        var updated = _sizes.ToArray(); updated[index] = size; _sizes = updated; LayoutPages();
    }
    private void LayoutPages()
    {
        _bounds = _mode == PdfPageLayoutMode.Spread
            ? DocumentViewportLayout.MeasureSpread(_sizes, _zoom, 24)
            : _mode == PdfPageLayoutMode.Single ? SinglePageBounds() :
                DocumentViewportLayout.Measure(_sizes, _zoom, 24);
        PageCanvas.Width = _bounds.Length == 0 ? 0 : _bounds.Max(r => r.Right);
        PageCanvas.Height = _bounds.Length == 0 ? 0 : _bounds.Max(r => r.Bottom);
        foreach (var (i, border) in _realized) Position(border, _bounds[i]);
        QueueRender();
    }
    private Rect[] SinglePageBounds()
    {
        var result = new Rect[_sizes.Count];
        if (_sizes.Count > 0)
        {
            var size = _sizes[_current];
            result[_current] = new Rect(0, 0, size.Width * _zoom, size.Height * _zoom);
        }
        return result;
    }
    private static void Position(Border border, Rect rect)
    { border.Width = rect.Width; border.Height = rect.Height; Canvas.SetLeft(border, rect.Left); Canvas.SetTop(border, rect.Top); }
    private void OnViewportChanged(object sender, SizeChangedEventArgs e) => QueueRender();
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_bounds.Length > 0 && _mode != PdfPageLayoutMode.Single)
        {
            double y = Math.Max(0, Scroller.VerticalOffset - 24) + Math.Min(Scroller.ViewportHeight * .25, 150);
            int current = DocumentViewportLayout.PageAtVerticalOffset(_bounds, y, _current);
            if (current >= 0) SelectPage(current);
        }
        QueueRender();
    }
    private void DetachActive()
    {
        if (ActivePageContent?.Parent is Viewbox box) box.Child = null;
    }
    private void SelectPage(int index)
    {
        if (_current == index) return;
        DetachActive();
        // Recreate the old image when it next enters the viewport; cache avoids decoding again.
        if (_realized.Remove(_current, out var old)) PageCanvas.Children.Remove(old);
        _current = index;
        if (_realized.Remove(index, out var selected)) PageCanvas.Children.Remove(selected);
        CurrentPageChanged?.Invoke(index);
    }
    private void QueueRender()
    {
        if (_render is null) return;
        _needsRender = true;
        if (_queued) return;
        _queued = true;
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            _queued = false;
            if (!_worker.IsCompleted) return;
            _needsRender = false;
            _worker = RenderVisibleAsync();
            try { await _worker; } catch (OperationCanceledException) { }
            catch (Exception ex) { DiagnosticLog.Failure("continuous-view", ex); }
            finally { if (_needsRender) QueueRender(); }
        }), DispatcherPriority.Background);
    }
    private async Task RenderVisibleAsync()
    {
        var token = _cancel.Token;
        long revision = _revision;
        var render = _render;
        if (render is null || Scroller.ViewportHeight <= 0) return;
        var viewport = new Rect(0, Math.Max(0, Scroller.VerticalOffset - 24), Math.Max(1, PageCanvas.Width), Scroller.ViewportHeight);
        int[] desired = _mode == PdfPageLayoutMode.Single ? [_current] :
            DocumentViewportLayout.VisibleRange(_bounds, viewport, 1);
        foreach (int index in _realized.Keys.Except(desired).ToArray())
        {
            if (index == _current) DetachActive();
            PageCanvas.Children.Remove(_realized[index]); _realized.Remove(index);
        }
        foreach (int index in desired.OrderBy(i => Math.Abs(i - _current)))
        {
            if (_realized.ContainsKey(index)) continue;
            token.ThrowIfCancellationRequested();
            var border = new Border { Background = Brushes.White, BorderBrush = PageEdge, BorderThickness = new(1), Effect = PageShadow };
            border.Child = new TextBlock { Text = $"Страница {index + 1} · загрузка…", Margin = new(18), Foreground = Brushes.SlateGray };
            Position(border, _bounds[index]); PageCanvas.Children.Add(border); _realized[index] = border;
            try
            {
                var bitmap = await _renderer.GetAsync(new(index, revision, (int)_bounds[index].Width), t => render(index, t), token);
                if (token.IsCancellationRequested || revision != _revision) return;
                if (index == _current && ActivePageContent is not null)
                {
                    DetachActive();
                    var box = new Viewbox { Stretch = Stretch.Fill, Child = ActivePageContent };
                    border.Child = box;
                }
                else border.Child = DecoratePage is null ? new Image { Source = bitmap, Stretch = Stretch.Fill }
                    : new Viewbox { Stretch = Stretch.Fill, Child = DecoratePage(index, bitmap) };
                PageRendered?.Invoke(index, bitmap);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                DiagnosticLog.Failure("continuous-page-render", ex);
                var retry = new Button { Content = $"Страница {index + 1}: повторить загрузку", VerticalAlignment = VerticalAlignment.Center, Margin = new(12) };
                retry.Click += (_, _) => { PageCanvas.Children.Remove(border); _realized.Remove(index); QueueRender(); };
                border.Child = retry;
            }
        }
        // A scroll during await may have changed the desired range.
        var now = new Rect(0, Math.Max(0, Scroller.VerticalOffset - 24), Math.Max(1, PageCanvas.Width), Scroller.ViewportHeight);
        if (!(_mode == PdfPageLayoutMode.Single ? new[] { _current } :
            DocumentViewportLayout.VisibleRange(_bounds, now, 1)).SequenceEqual(desired))
            _ = Dispatcher.BeginInvoke(new Action(QueueRender), DispatcherPriority.ContextIdle);
    }
}
