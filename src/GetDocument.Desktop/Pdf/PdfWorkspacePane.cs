using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GetDocument.Core.Preview;
using GetDocument.Desktop.Preview;
using GetPdf;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace GetDocument.Desktop.Pdf;

/// <summary>
/// The PDF tab — GET PDF's workspace on Avalonia: continuous pages with thumbnails, search
/// with highlights, text selection and copy, page operations, stamps, page numbering,
/// rectangles and notes, undo/redo, qpdf compression, signature check, form filling and
/// comparison. Every change is a plan over a private working copy; "Сохранить как…"
/// writes a NEW PDF through GET PDF's own writer, the original is never modified.
/// </summary>
internal sealed partial class PdfWorkspacePane : DockPanel, IDisposable
{
    private const double FitPadding = 64;
    private static readonly double[] ZoomSteps = [0.5, 0.75, 1, 1.25, 1.5, 2, 3];

    private readonly string _original;
    private readonly string _sessionFolder;
    private readonly Action<string> _status;
    private readonly Func<string, Task> _openPath;
    private readonly CancellationTokenSource _lifetime = new();
    private string _working;
    private PdfRaster _raster;
    private readonly PdfPagePlan _plan;
    private readonly PdfEditHistory _history;
    private bool[] _markupAllowed;
    private readonly bool _canModify;
    private readonly string? _modifyBlockedReason;
    private string _savedFingerprint;

    // view
    private readonly ScrollViewer _scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel _pagesHost = new() { Margin = new Thickness(24, 24, 24, 8), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly List<PdfPageView> _pageViews = [];
    private readonly ListBox _thumbs = new() { SelectionMode = SelectionMode.Single };
    private readonly Border _thumbPane;
    private readonly Border _toolPane;
    private readonly TextBox _search = new() { PlaceholderText = "Поиск по документу", Width = 210 };
    private readonly TextBox _pageBox = new() { Width = 56, TextAlignment = TextAlignment.Center, MinHeight = 40 };
    private readonly TextBlock _pageCount = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _note = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _undo, _redo, _save, _favorite, _editor, _selectText, _zoomMenu;
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private bool _fitWidth = true;
    private double _zoom = 1;
    private int _current;
    private bool _syncingThumbs;
    private Mode _mode = Mode.Browse;
    private CancellationTokenSource? _render;

    // search
    private string _query = "";
    private IReadOnlyList<PdfTextPageMatch> _matches = [];
    private int _matchIndex = -1;

    // selection
    private readonly Dictionary<(int Source, int Rotation), PdfWordSelection> _words = [];
    private int _selectionPage = -1, _anchor = -1, _end = -1;
    private Point? _dragStart;

    private enum Mode { Browse, SelectText, Rectangle, Note }

    public bool HasUnsavedChanges => _plan.HasChanges && Fingerprint() != _savedFingerprint;
    public string OriginalPath => _original;
    public event Action<bool>? FavoriteToggleRequested;

    public PdfWorkspacePane(string path, Action<string> status, Func<string, Task> openPath, bool isFavorite)
    {
        _original = Path.GetFullPath(path);
        _status = status;
        _openPath = openPath;
        _sessionFolder = Path.Combine(Path.GetTempPath(), "getdoc-pdf-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_sessionFolder);
        _working = Path.Combine(_sessionFolder, "source.pdf");
        File.Copy(_original, _working);
        _raster = new PdfRaster(_working);
        _plan = new PdfPagePlan(_raster.PageSizes.Count);
        _history = new PdfEditHistory(_plan);
        _savedFingerprint = Fingerprint();
        try
        {
            PdfStructureGuard.EnsureSafeToRewrite(_working);
            _canModify = true;
            _markupAllowed = PdfMarkupPolicy.ReadEligibleSourcePages(_working);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or
                                   PdfSharp.Pdf.IO.PdfReaderException)
        {
            _canModify = false;
            _markupAllowed = new bool[_raster.PageSizes.Count];
            _modifyBlockedReason = ex.Message;
        }

        // ---- toolbar
        _undo = IconButton(Icons.Undo, "Отменить (Ctrl+Z)", () => Apply(() => _history.Undo()));
        _redo = IconButton(Icons.Redo, "Повторить (Ctrl+Y)", () => Apply(() => _history.Redo()));
        _favorite = IconButton(Icons.Star, "Добавить в избранное", () => FavoriteToggleRequested?.Invoke(true));
        SetFavorite(isFavorite);
        _save = TextButton("Сохранить как…", Icons.Save, "Сохранить новую копию PDF со всеми изменениями (Ctrl+S)", () => _ = SaveAsAsync());
        _editor = TextButton("Редактирование", Icons.Sliders, "Показать или скрыть инструменты редактирования", ToggleEditor);
        _selectText = TextButton("Выделение текста", Icons.Cursor, "Выделить текст мышью и скопировать (Ctrl+C)", ToggleSelection);
        _zoomMenu = TextButton("По ширине", Icons.ZoomIn, "Масштаб и режим просмотра", () => { });
        _zoomMenu.Flyout = BuildZoomMenu();
        Avalonia.Automation.AutomationProperties.SetName(_search, "Поиск по документу");
        Avalonia.Automation.AutomationProperties.SetName(_pageBox, "Номер страницы");
        ToolTip.SetTip(_pageBox, "Номер страницы; Enter — перейти (Ctrl+G)");
        var findPrev = IconButton(Icons.ChevronUp, "Предыдущее совпадение (Shift+F3)", () => FindNext(true));
        var findNext = IconButton(Icons.ChevronDown, "Следующее совпадение (F3)", () => FindNext(false));
        _search.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if ((e.KeyModifiers & KeyModifiers.Shift) != 0) FindNext(true);
            else await SearchAsync();
        };
        _pageBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (int.TryParse(_pageBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) &&
                n >= 1 && n <= _plan.Pages.Count) GoTo(n - 1);
            else _status($"Номер страницы — от 1 до {_plan.Pages.Count}.");
        };
        // Wraps to a second row on narrow windows instead of hiding commands behind a scrollbar.
        var left = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            ItemSpacing = 4, LineSpacing = 4 };
        left.Children.Add(IconButton(Icons.Columns, "Показать или скрыть миниатюры страниц", ToggleThumbs));
        left.Children.Add(Divider());
        left.Children.Add(_search);
        left.Children.Add(findPrev);
        left.Children.Add(findNext);
        left.Children.Add(Divider());
        left.Children.Add(IconButton(Icons.ChevronLeft, "Предыдущая страница", () => GoTo(_current - 1)));
        left.Children.Add(_pageBox);
        left.Children.Add(_pageCount);
        left.Children.Add(IconButton(Icons.ChevronRight, "Следующая страница", () => GoTo(_current + 1)));
        left.Children.Add(Divider());
        left.Children.Add(IconButton(Icons.Minus, "Уменьшить (Ctrl −)", () => ChangeZoom(-1)));
        left.Children.Add(IconButton(Icons.Plus, "Увеличить (Ctrl +)", () => ChangeZoom(1)));
        left.Children.Add(_zoomMenu);
        left.Children.Add(Divider());
        left.Children.Add(_selectText);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(_undo);
        right.Children.Add(_redo);
        right.Children.Add(_favorite);
        right.Children.Add(_save);
        right.Children.Add(_editor);
        var bar = new DockPanel { Margin = new Thickness(16, 8) };
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        bar.Children.Add(left);
        var toolbar = new Border { Child = bar };
        toolbar.Classes.Add("header");
        SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);

        // ---- status line of the tab
        var info = new Border { Padding = new Thickness(20, 6), Child = _note };
        info.Classes.Add("header");
        _note.Classes.Add("hint");
        SetDock(info, Dock.Top);
        Children.Add(info);

        // ---- thumbnails
        _thumbs.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<PdfThumb>((thumb, _) => thumb?.Visual, true);
        _thumbs.SelectionChanged += (_, _) =>
        {
            if (_syncingThumbs || _thumbs.SelectedIndex < 0) return;
            GoTo(_thumbs.SelectedIndex);
        };
        _thumbs.ContainerPrepared += (_, e) => _ = RenderThumbAsync(e.Index);
        _thumbs.ContextMenu = PageContextMenu();
        _thumbPane = new Border { Width = 190, Child = _thumbs, BorderThickness = new Thickness(0, 0, 1, 0) };
        _thumbPane.Classes.Add("header");
        SetDock(_thumbPane, Dock.Left);
        Children.Add(_thumbPane);

        // ---- editor tools
        _toolPane = new Border { Width = 320, IsVisible = false, BorderThickness = new Thickness(1, 0, 0, 0),
            Child = BuildTools() };
        _toolPane.Classes.Add("header");
        SetDock(_toolPane, Dock.Right);
        Children.Add(_toolPane);

        // ---- pages
        _scroller.Content = _pagesHost;
        _scroller.Background = (IBrush)Application.Current!.FindResource("GetCanvasBrush")!;
        _scroller.ScrollChanged += (_, _) => OnScrolled();
        _scroller.SizeChanged += (_, _) => { if (_fitWidth) Relayout(); };
        _scroller.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;
            e.Handled = true;
            ChangeZoom(e.Delta.Y > 0 ? 1 : -1);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Children.Add(_scroller);
        _renderTimer.Tick += (_, _) => { _renderTimer.Stop(); _ = RenderVisibleAsync(); };

        KeyDown += OnKeyDown;
        Rebuild();
        UpdateState();
        _note.Text = _canModify
            ? "Изменения применяются к копии. Оригинал не меняется — сохраните результат через «Сохранить как…»."
            : "Только просмотр: " + _modifyBlockedReason + " Доступны поиск, выделение текста, печать, проверка подписей и заполнение формы.";
    }

    // ------------------------------------------------------------------ layout & rendering

    private void Rebuild()
    {
        _pagesHost.Children.Clear();
        foreach (var view in _pageViews) view.ClearImage();
        _pageViews.Clear();
        var thumbs = new List<PdfThumb>();
        for (int i = 0; i < _plan.Pages.Count; i++)
        {
            var view = new PdfPageView();
            int index = i;
            view.PointerPressed += (s, e) => OnPagePressed(view, e);
            view.PointerMoved += (s, e) => OnPageMoved(view, e);
            view.PointerReleased += (s, e) => OnPageReleased(view, e);
            view.ContextMenu = PageContextMenu();
            _pageViews.Add(view);
            _pagesHost.Children.Add(view);
            thumbs.Add(new PdfThumb(i, _plan.Pages[i]));
        }
        _syncingThumbs = true;
        _thumbs.ItemsSource = thumbs;
        _syncingThumbs = false;
        _words.Clear();
        ClearSelection();
        Relayout();
    }

    private double DisplayWidth(PdfPageReference page)
    {
        var points = _raster.PageSizes[page.SourcePage];
        double pointsWidth = page.Rotation is 90 or 270 ? points.Height : points.Width;
        if (_fitWidth)
        {
            double available = Math.Max(240, _scroller.Bounds.Width - FitPadding);
            return Math.Min(available, 1600);
        }
        return pointsWidth * 96 / 72 * _zoom;
    }

    private void Relayout()
    {
        for (int i = 0; i < _pageViews.Count; i++)
        {
            var reference = _plan.Pages[i];
            _pageViews[i].Configure(i, reference, _raster.PageSizes[reference.SourcePage], DisplayWidth(reference));
        }
        _zoomMenu.Content = MenuCaption(_fitWidth ? "По ширине" : $"{_zoom * 100:0} %");
        ApplySearchHighlight();
        ScheduleRender();
    }

    private void ScheduleRender()
    {
        _renderTimer.Stop();
        _renderTimer.Start();
    }

    private (int First, int Last) VisibleRange()
    {
        if (_pageViews.Count == 0) return (0, -1);
        double top = _scroller.Offset.Y, bottom = top + _scroller.Viewport.Height;
        int first = -1, last = -1;
        double y = _pagesHost.Margin.Top;
        for (int i = 0; i < _pageViews.Count; i++)
        {
            double h = _pageViews[i].Height + _pageViews[i].Margin.Bottom;
            if (y + h >= top && y <= bottom)
            {
                if (first < 0) first = i;
                last = i;
            }
            else if (y > bottom) break;
            y += h;
        }
        return first < 0 ? (0, 0) : (first, last);
    }

    private void OnScrolled()
    {
        var (first, last) = VisibleRange();
        // The "current" page is the one occupying the upper third of the viewport.
        double probe = _scroller.Offset.Y + _scroller.Viewport.Height / 3;
        double y = _pagesHost.Margin.Top;
        int current = first;
        for (int i = 0; i < _pageViews.Count; i++)
        {
            double h = _pageViews[i].Height + _pageViews[i].Margin.Bottom;
            if (probe < y + h) { current = i; break; }
            y += h;
        }
        SetCurrent(current, scroll: false);
        ScheduleRender();
    }

    private async Task RenderVisibleAsync()
    {
        if (_lifetime.IsCancellationRequested || _pageViews.Count == 0) return;
        _render?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _render = cts;
        var (first, last) = VisibleRange();
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var order = Enumerable.Range(first, Math.Max(0, last - first + 1))
            .Concat([last + 1, first - 1]).Where(i => i >= 0 && i < _pageViews.Count).Distinct().ToList();
        // Release rasters far from the viewport to keep memory flat on 1000-page documents.
        for (int i = 0; i < _pageViews.Count; i++)
            if ((i < first - 4 || i > last + 4) && _pageViews[i].HasImage) _pageViews[i].ClearImage();
        foreach (int i in order)
        {
            if (cts.IsCancellationRequested) return;
            var view = _pageViews[i];
            var reference = view.Reference;
            bool sideways = reference.Rotation is 90 or 270;
            int pixelWidth = (int)Math.Ceiling((sideways ? view.Height : view.Width) * scaling);
            if (view.HasImage && Math.Abs(view.RenderedWidth - pixelWidth) < 8) continue;
            try
            {
                var bitmap = await Task.Run(() => _raster.Render(reference.SourcePage, pixelWidth, cts.Token), cts.Token);
                if (cts.IsCancellationRequested || !ReferenceEquals(view.Reference, reference)) { bitmap.Dispose(); return; }
                view.SetImage(bitmap, pixelWidth);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
            {
                _status("Страница " + (i + 1) + " не отрисована: " + ex.Message);
            }
        }
    }

    private async Task RenderThumbAsync(int index)
    {
        if (_thumbs.ItemsSource is not IReadOnlyList<PdfThumb> thumbs || index < 0 || index >= thumbs.Count) return;
        var thumb = thumbs[index];
        if (thumb.HasImage) return;
        try
        {
            double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            var bitmap = await Task.Run(() => _raster.Render(thumb.Reference.SourcePage, (int)(132 * scaling), _lifetime.Token));
            thumb.SetImage(bitmap);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException) { }
    }

    // ------------------------------------------------------------------ navigation & zoom

    public void GoTo(int index)
    {
        if (_pageViews.Count == 0) return;
        index = Math.Clamp(index, 0, _pageViews.Count - 1);
        double y = _pagesHost.Margin.Top;
        for (int i = 0; i < index; i++) y += _pageViews[i].Height + _pageViews[i].Margin.Bottom;
        _scroller.Offset = new Vector(_scroller.Offset.X, Math.Max(0, y - 8));
        SetCurrent(index, scroll: false);
    }

    private void SetCurrent(int index, bool scroll)
    {
        _current = Math.Clamp(index, 0, Math.Max(0, _plan.Pages.Count - 1));
        if (!_pageBox.IsKeyboardFocusWithin) _pageBox.Text = (_current + 1).ToString(CultureInfo.InvariantCulture);
        _pageCount.Text = $"из {_plan.Pages.Count}";
        _syncingThumbs = true;
        _thumbs.SelectedIndex = _current;
        _syncingThumbs = false;
        if (scroll) GoTo(_current);
        UpdateState();
    }

    public void ChangeZoom(int delta)
    {
        double current = _fitWidth ? (_pageViews.Count > 0
            ? _pageViews[_current].Width / Math.Max(1, (_plan.Pages[_current].Rotation is 90 or 270
                ? _raster.PageSizes[_plan.Pages[_current].SourcePage].Height
                : _raster.PageSizes[_plan.Pages[_current].SourcePage].Width) * 96 / 72) : 1) : _zoom;
        int step = delta > 0
            ? Array.FindIndex(ZoomSteps, z => z > current + 0.01)
            : Array.FindLastIndex(ZoomSteps, z => z < current - 0.01);
        if (step < 0) return;
        _fitWidth = false;
        _zoom = ZoomSteps[step];
        int keep = _current;
        Relayout();
        Dispatcher.UIThread.Post(() => GoTo(keep), DispatcherPriority.Background);
    }

    public void ResetZoom()
    {
        _fitWidth = true;
        int keep = _current;
        Relayout();
        Dispatcher.UIThread.Post(() => GoTo(keep), DispatcherPriority.Background);
    }

    private MenuFlyout BuildZoomMenu()
    {
        var menu = new MenuFlyout();
        void Add(string text, Action action)
        {
            var item = new MenuItem { Header = text };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("По ширине окна", ResetZoom);
        Add("Страница целиком", FitPage);
        Add("100 %", () => SetZoom(1));
        Add("150 %", () => SetZoom(1.5));
        Add("200 %", () => SetZoom(2));
        return menu;
    }

    private void SetZoom(double zoom)
    {
        _fitWidth = false;
        _zoom = zoom;
        int keep = _current;
        Relayout();
        Dispatcher.UIThread.Post(() => GoTo(keep), DispatcherPriority.Background);
    }

    private void FitPage()
    {
        if (_pageViews.Count == 0) return;
        var reference = _plan.Pages[_current];
        var points = _raster.PageSizes[reference.SourcePage];
        bool sideways = reference.Rotation is 90 or 270;
        double w = (sideways ? points.Height : points.Width) * 96 / 72, h = (sideways ? points.Width : points.Height) * 96 / 72;
        SetZoom(Math.Max(0.2, Math.Min((_scroller.Bounds.Width - FitPadding) / w, (_scroller.Bounds.Height - 40) / h)));
    }

    public void FocusSearch()
    {
        _search.Focus();
        _search.SelectAll();
    }

    public void FocusPageNumber()
    {
        _pageBox.Focus();
        _pageBox.SelectAll();
    }

    // ------------------------------------------------------------------ plan changes

    private void Apply(Func<bool> change)
    {
        if (!change()) return;
        AfterPlanChange();
    }

    private void Change(Action<PdfPagePlan> change, string done)
    {
        if (!_canModify)
        {
            _status("Этот PDF можно только просматривать: " + _modifyBlockedReason);
            return;
        }
        try
        {
            _history.Execute(change);
            AfterPlanChange();
            _status(done);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _status(ex.Message);
        }
    }

    private void AfterPlanChange()
    {
        int keep = Math.Min(_current, _plan.Pages.Count - 1);
        // Structural changes (order/count/rotation) rebuild views; markup only redraws overlays.
        bool structural = _pageViews.Count != _plan.Pages.Count ||
            _pageViews.Select((v, i) => v.Reference.SourcePage != _plan.Pages[i].SourcePage ||
                                        v.Reference.Rotation != _plan.Pages[i].Rotation).Any(x => x);
        if (structural) Rebuild();
        else
        {
            for (int i = 0; i < _pageViews.Count; i++)
            {
                _pageViews[i].Configure(i, _plan.Pages[i], _raster.PageSizes[_plan.Pages[i].SourcePage],
                    DisplayWidth(_plan.Pages[i]));
            }
            // Thumbnail captions show "штамп · № · разметка": refresh them, keep rendered images.
            if (_thumbs.ItemsSource is IReadOnlyList<PdfThumb> old && old.Count == _plan.Pages.Count)
            {
                var fresh = _plan.Pages.Select((p, i) => old[i].WithReference(i, p)).ToList();
                _syncingThumbs = true;
                _thumbs.ItemsSource = fresh;
                _thumbs.SelectedIndex = _current;
                _syncingThumbs = false;
            }
        }
        Dispatcher.UIThread.Post(() => GoTo(keep), DispatcherPriority.Background);
        UpdateState();
    }

    private string Fingerprint() => string.Join("|", _plan.Pages.Select(p =>
        $"{p.SourcePage}:{p.Rotation}:{p.Stamp}:{p.PageNumber}@{p.NumberPosition}:" +
        string.Join(";", (p.Rectangles ?? []).Select(r => $"{r.X:0.###},{r.Y:0.###},{r.Width:0.###},{r.Height:0.###}")) + ":" +
        string.Join(";", (p.Notes ?? []).Select(n => $"{n.X:0.###},{n.Y:0.###},{n.Text}"))));

    private void UpdateState()
    {
        _undo.IsEnabled = _history.CanUndo;
        _redo.IsEnabled = _history.CanRedo;
        bool changes = HasUnsavedChanges;
        _save.Classes.Set("primary", changes);
        _selectText.Classes.Set("active", _mode == Mode.SelectText);
        _editor.Classes.Set("active", _toolPane.IsVisible);
        UpdateToolState();
    }

    public void SetFavorite(bool favorite)
    {
        _favorite.Classes.Set("active", favorite);
        string tip = favorite ? "Убрать из избранного" : "Добавить в избранное";
        ToolTip.SetTip(_favorite, tip);
        Avalonia.Automation.AutomationProperties.SetName(_favorite, tip);
        if (_favorite.Content is Viewbox { Child: Canvas { Children: [Avalonia.Controls.Shapes.Path star] } })
            star.Fill = favorite ? new SolidColorBrush(Color.Parse("#FBBA00")) : null;
    }

    private bool CanMarkCurrent(out string reason)
    {
        reason = "";
        if (!_canModify) { reason = "Этот PDF можно только просматривать."; return false; }
        var page = _plan.Pages[_current];
        if (page.Rotation != 0)
        {
            reason = "Рамки и заметки ставятся на страницу без поворота. Сначала верните страницу в исходное положение.";
            return false;
        }
        if (page.SourcePage >= _markupAllowed.Length || !_markupAllowed[page.SourcePage])
        {
            reason = "На этой странице разметка недоступна: у неё нестандартная геометрия (кадрирование или собственный поворот).";
            return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ keyboard

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        if (ctrl && e.Key == Key.Z) { e.Handled = true; Apply(() => _history.Undo()); }
        else if (ctrl && e.Key == Key.Y) { e.Handled = true; Apply(() => _history.Redo()); }
        else if (ctrl && e.Key == Key.S) { e.Handled = true; _ = SaveAsAsync(); }
        else if (ctrl && e.Key == Key.C && _anchor >= 0) { e.Handled = true; _ = CopySelectionAsync(); }
        else if (e.Key == Key.PageDown) { e.Handled = true; GoTo(_current + 1); }
        else if (e.Key == Key.PageUp) { e.Handled = true; GoTo(_current - 1); }
        else if (e.Key == Key.Escape && _mode != Mode.Browse) { e.Handled = true; SetMode(Mode.Browse); }
        else if (e.Key == Key.F3) { e.Handled = true; FindNext((e.KeyModifiers & KeyModifiers.Shift) != 0); }
    }

    // ------------------------------------------------------------------ small UI helpers

    private static Button IconButton(Geometry icon, string tip, Action action)
    {
        var button = new Button { Content = Icons.Create(icon, 18), Width = 40, MinHeight = 40, Padding = new Thickness(0) };
        button.Classes.Add("ghost");
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    private static Button TextButton(string text, Geometry icon, string tip, Action action)
    {
        var button = new Button { Content = MenuCaption(text, icon), MinHeight = 40 };
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, text);
        button.Click += (_, _) => action();
        return button;
    }

    private static StackPanel MenuCaption(string text, Geometry? icon = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (icon is not null) content.Children.Add(Icons.Create(icon, 18));
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return content;
    }

    private static Border Divider() => new()
    {
        Width = 1, Height = 26, Margin = new Thickness(8, 0),
        Background = (IBrush)Application.Current!.FindResource("GetBorderBrush")!
    };

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    public void Dispose()
    {
        _lifetime.Cancel();
        _renderTimer.Stop();
        foreach (var view in _pageViews) view.ClearImage();
        if (_thumbs.ItemsSource is IEnumerable<PdfThumb> thumbs)
            foreach (var thumb in thumbs) thumb.Dispose();
        try { Directory.Delete(_sessionFolder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Thumbnail list entry: lazily rendered image + page label.</summary>
internal sealed class PdfThumb : IDisposable
{
    private readonly Image _image = new() { Width = 132, Stretch = Stretch.Uniform };
    public PdfThumb(int index, PdfPageReference reference)
    {
        Reference = reference;
        var label = new TextBlock { Text = $"Страница {index + 1}", HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        var frame = new Border
        {
            Background = Brushes.White, BorderThickness = new Thickness(1), MinHeight = 60,
            BorderBrush = new SolidColorBrush(Color.Parse("#D5DAE5")), HorizontalAlignment = HorizontalAlignment.Center,
            Child = new LayoutTransformControl
            {
                Child = _image,
                LayoutTransform = reference.Rotation == 0 ? null : new RotateTransform(reference.Rotation)
            }
        };
        var marks = new List<string>();
        if (reference.Stamp.Length > 0) marks.Add("штамп");
        if (reference.PageNumber.Length > 0) marks.Add("№");
        if (reference.Rectangles is { Count: > 0 } || reference.Notes is { Count: > 0 }) marks.Add("разметка");
        var panel = new StackPanel { Margin = new Thickness(6), Children = { frame, label } };
        if (marks.Count > 0)
            panel.Children.Add(new TextBlock { Text = string.Join(" · ", marks), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse("#00809A")) });
        Visual = panel;
        Avalonia.Automation.AutomationProperties.SetName(panel, label.Text);
    }

    public PdfPageReference Reference { get; }
    public Control Visual { get; }
    public bool HasImage => _image.Source is not null;

    /// <summary>Same page with new markup captions; the rendered image moves to the new entry.</summary>
    public PdfThumb WithReference(int index, PdfPageReference reference)
    {
        var next = new PdfThumb(index, reference);
        if (_image.Source is Bitmap bitmap && reference.SourcePage == Reference.SourcePage && reference.Rotation == Reference.Rotation)
        {
            next._image.Source = bitmap;
            _image.Source = null;
        }
        return next;
    }
    public void SetImage(Bitmap bitmap) { (_image.Source as IDisposable)?.Dispose(); _image.Source = bitmap; }
    public void Dispose() { (_image.Source as IDisposable)?.Dispose(); _image.Source = null; }
}
