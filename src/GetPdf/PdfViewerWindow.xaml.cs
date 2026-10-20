using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.IO.Path;
using Microsoft.Win32;
using NAPS2.Images;
using NAPS2.Images.Wpf;
using NAPS2.ImportExport;
using NAPS2.Pdf;
using NAPS2.Ocr;
using NAPS2.Scan;

namespace GetPdf;

/// <summary>
/// A separate PDF workspace: it reads originals for preview, but writes page-object copies
/// rather than exporting rendered pixels. Editing source text and annotations comes later.
/// </summary>
public partial class PdfViewerWindow : UserControl
{
    private readonly string? _initialPath;
    private PdfLazyPageStore? _pageStore;
    private bool[] _markupAllowed = Array.Empty<bool>();
    private Point? _dragStart;
    private Rectangle? _dragPreview;
    private PdfEditHistory? _history;
    // Experimental native /Annots drafts. Purely transient preview; NEVER in PDF save paths.
    private readonly PdfNativeAnnotationDraftSession _nativeDrafts = new();
    private string? _nativeDraftMode;
    private readonly PdfRenderRequests _renderRequests = new();
    private readonly PdfJobProgressViewModel _jobProgress = new();
    private readonly PdfWorkspaceViewModel _workspace = new();
    private bool _rendering = false;
    private bool _canModify;
    private ScanningContext? _context;
    private string? _sessionFolder;
    private readonly PdfScratchSessionLifecycle _scratchSession = new();
    private string? _originalPath;
    private string? _draftSourceHash;
    private bool _draftWarningShown;
    private string? _sourcePath;
    private string? _originalSourceCopyPath;
    private PdfPagePlan? _plan;
    private int _pageIndex;
    private double _zoom = 1.0;
    private bool _loading;
    private CancellationTokenSource? _batchCancel;
    private bool _closing;
    private bool _refreshingList;
    private string? _savedPlanFingerprint;
    private PdfTextIndex? _textIndex;
    private IReadOnlyList<PdfTextHit> _searchHits = Array.Empty<PdfTextHit>();
    private int _activeHit;
    private string _activeSearchQuery = string.Empty;
    private readonly Dictionary<(int Source, string Query, int Rotation),
        IReadOnlyList<PdfSearchHighlightRect>> _searchGeometryCache = new();
    private bool _refreshingSearchResults;
    private readonly HashSet<PdfThumbnailItem> _visibleThumbnails = [];
    private IReadOnlyList<PdfThumbnailItem> _thumbnailItems = [];
    private PdfLruCache<(int SourcePage, int Rotation), BitmapSource>? _thumbnailCache;
    private CancellationTokenSource _thumbnailCancel = new();
    private Task _thumbnailWorker = Task.CompletedTask;
    private bool? _pagePaneOverride;
    private bool _editingToolsVisible;
    private bool _initializedWorkspace;
    private Task? _initialLoadTask;
    private bool _showOutline;
    private IReadOnlyList<PdfOutlineReader.Node> _pdfOutline = Array.Empty<PdfOutlineReader.Node>();

    private string? CurrentPlanFingerprint() =>
        _plan is null ? null : string.Join(";", _plan.Pages.Select(p =>
            $"{p.SourcePage}:{p.Rotation}:{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(p.Stamp))}:" +
            string.Join("|", (p.Rectangles ?? Array.Empty<PdfRectangleMarkup>()).Select(r =>
                FormattableString.Invariant($"{r.X:R},{r.Y:R},{r.Width:R},{r.Height:R}"))) + ":" +
            string.Join("|", (p.Notes ?? Array.Empty<PdfTextNote>()).Select(n =>
                FormattableString.Invariant($"{n.X:R},{n.Y:R}") + "," +
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(n.Text))))));

    internal static long EstimateBitmapBytes(BitmapSource bitmap) =>
        checked((long)bitmap.PixelWidth * bitmap.PixelHeight *
            Math.Max(1, (bitmap.Format.BitsPerPixel + 7) / 8));

    private Window HostWindow => Window.GetWindow(this) ??
        Application.Current?.MainWindow ??
        throw new InvalidOperationException("Окно ГЭТ PDF недоступно.");
    internal string? CurrentDocumentPath => _originalPath ?? _initialPath;
    internal bool IsEmpty => _sourcePath is null && !_loading && !_closing;
    internal bool IsBusy => _loading || _rendering || _batchCancel is not null || _closing;

    private bool HasPendingChanges => _nativeDrafts.Count > 0 ||
        (_plan?.HasChanges == true &&
        !string.Equals(CurrentPlanFingerprint(), _savedPlanFingerprint, StringComparison.Ordinal));

    public PdfViewerWindow(string? initialPath = null, DocumentSession? session = null, string? displayName = null)
    {
        if (!WorkstationPolicy.Current.AllowPdfViewer)
            throw new InvalidOperationException("Просмотр PDF отключён политикой ИТ-службы.");
        _initialPath = initialPath;
        _documentSession = session ?? new DocumentSession();
        _displayName = displayName;
        InitializeComponent();
        DataContext = _workspace;
        InitializeContinuousView();
        RecentButton.Visibility = WorkstationPolicy.Current.RememberRecentDocuments
            ? Visibility.Visible : Visibility.Collapsed;
        Gup2PortalButton.Visibility = WorkstationPolicy.Current.AllowGup2Portal
            ? Visibility.Visible : Visibility.Collapsed;
        Gup2VerifyShaButton.Visibility = WorkstationPolicy.Current.AllowGup2Portal
            ? Visibility.Visible : Visibility.Collapsed;
        PdfJobProgressBar.DataContext = _jobProgress;
        var preferences = PdfWorkspacePreferencesStore.Load();
        ActiveToolIndex = PdfWorkspaceToolGroups.ToIndex(preferences.ActiveTool);
        LayoutModeBox.SelectedIndex = preferences.ViewMode;
        DocumentView.LayoutMode = (PdfPageLayoutMode)preferences.ViewMode;
        UpdateToolMode();
        ApplyResponsiveLayout();
        SetControls();
    }

    private void PdfToolMode_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateToolMode();

    private void UpdateToolMode()
    {
        // WPF may raise SelectionChanged while loading XAML. The groups only
        // become available after InitializeComponent has finished.
        if (MarkupTools is null || PageTools is null ||
            DocumentTools is null || PdfToolModeBox is null) return;
        int index = ActiveToolIndex;
        MarkupTools.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageTools.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        DocumentTools.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        if (SourceTextTools is not null) SourceTextTools.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        if (index == 4) _ = LoadTextFragmentsAsync();
    }

    private void LayoutMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DocumentView is null || LayoutModeBox is null || LayoutModeBox.SelectedIndex < 0) return;
        DocumentView.LayoutMode = (PdfPageLayoutMode)LayoutModeBox.SelectedIndex;
        if (_plan is not null) ShowPage();
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (HostWindow is PdfTabbedWindow shell) shell.ToggleFullscreen();
    }

    private void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        if (_originalPath is null || _plan is null || _loading) return;
        try
        {
            bool added = PdfPageBookmarks.Toggle(_originalPath, _pageIndex);
            BookmarkButton.Content = added ? "Закладка ★" : "Закладка ☆";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Failure("pdf-bookmark-write", ex);
            StatusText.Text = "Не удалось сохранить закладку.";
        }
    }

    private void BookmarksMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button owner || _originalPath is null || _plan is null) return;
        var menu = new ContextMenu();
        foreach (int page in PdfPageBookmarks.Read(_originalPath).Where(i => i < _plan.Pages.Count))
        {
            int selected = page;
            var item = new MenuItem { Header = $"Страница {selected + 1}" };
            item.Click += (_, _) =>
            {
                if (_plan is null || selected >= _plan.Pages.Count) return;
                _pageIndex = selected;
                RefreshList();
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Закладок пока нет", IsEnabled = false });
        owner.ContextMenu = menu;
        menu.PlacementTarget = owner;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ToggleEditor_Click(object sender, RoutedEventArgs e)
    {
        _editingToolsVisible = !_editingToolsVisible;
        if (_editingToolsVisible && PdfToolModeBox.SelectedIndex == 0)
            PdfToolModeBox.SelectedIndex = 2; // Page tools on first entry, not an empty panel.
        PdfToolModeBox.Visibility = _editingToolsVisible ? Visibility.Visible : Visibility.Collapsed;
        EditModeButton.Content = _editingToolsVisible ? "Закрыть редактор" : "Редактирование";
        UndoButton.Visibility = _editingToolsVisible ? Visibility.Visible : Visibility.Collapsed;
        RedoButton.Visibility = _editingToolsVisible ? Visibility.Visible : Visibility.Collapsed;
        SaveAsButton.Visibility = _editingToolsVisible ? Visibility.Visible : Visibility.Collapsed;
        ApplyResponsiveLayout();
    }

    private void Viewer_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout();

    private void ApplyResponsiveLayout()
    {
        if (PagePaneColumn is null || PagePaneBorder is null ||
            PagePaneToggleButton is null || SearchResultsList is null) return;

        bool show = PdfWorkspaceLayout.ShowPages(ActualWidth, _pagePaneOverride);
        bool wasVisible = PagePaneBorder.Visibility == Visibility.Visible;
        PagePaneBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PagePaneColumn.Width = new GridLength(show ? 205 : 0);
        ToolPaneColumn.Width = new GridLength(_editingToolsVisible ? (ActualWidth < 1000 ? 230 : 280) : 0);
        PagePaneToggleButton.Content = show ? "Скрыть страницы"
            : _searchHits.Count > 0 ? $"Результаты ({_searchHits.Count})" : "Страницы";
        SearchResultsList.Height = PdfWorkspaceLayout.ResultsHeight(ActualHeight);
        if (!show && (PageList.IsKeyboardFocusWithin || PdfOutlineTree.IsKeyboardFocusWithin ||
                      SearchResultsList.IsKeyboardFocusWithin))
            PdfPreviewScroll.Focus();
        if (!show && wasVisible)
        {
            foreach (var entry in _visibleThumbnails.ToArray())
                UntrackThumbnail(entry);
        }
        if (show && !wasVisible)
        {
            // Visibility changes do not reliably fire Image.Loaded on a recycled
            // container. Re-register only realized visual children, never all pages.
            Dispatcher.BeginInvoke(new Action(RestoreRealizedThumbnails), DispatcherPriority.Loaded);
        }
    }

    private void RestoreRealizedThumbnails()
    {
        if (_closing || _pageStore is null || PagePaneBorder.Visibility != Visibility.Visible)
            return;

        void Visit(DependencyObject parent)
        {
            if (parent is ListBoxItem { DataContext: PdfThumbnailItem item })
            {
                if (_thumbnailItems.Contains(item)) TrackThumbnail(item);
                return;
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                Visit(VisualTreeHelper.GetChild(parent, i));
        }

        Visit(PageList);
        EnsureThumbnailWorker();
    }

    private void ShowPagePane_Click(object sender, RoutedEventArgs e)
    {
        _showOutline = false;
        _pagePaneOverride = true;
        UpdateOutlinePane();
    }

    private void ShowOutlinePane_Click(object sender, RoutedEventArgs e)
    {
        _showOutline = true;
        _pagePaneOverride = true;
        UpdateOutlinePane();
    }

    private void UpdateOutlinePane()
    {
        if (PdfOutlineTree is null || PageList is null || SearchResultsPanel is null) return;
        PdfOutlineTree.Visibility = _showOutline ? Visibility.Visible : Visibility.Collapsed;
        PageList.Visibility = _showOutline ? Visibility.Collapsed : Visibility.Visible;
        SearchResultsPanel.Visibility = _showOutline || _searchHits.Count == 0
            ? Visibility.Collapsed : Visibility.Visible;
        if (ShowOutlineButton is not null)
            ShowOutlineButton.FontWeight = _showOutline ? FontWeights.Bold : FontWeights.Normal;
        if (ShowPagesButton is not null)
            ShowPagesButton.FontWeight = _showOutline ? FontWeights.Normal : FontWeights.Bold;
        ApplyResponsiveLayout();
    }

    private void PdfOutline_SelectedItemChanged(object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (_loading || _closing || _plan is null ||
            e.NewValue is not PdfOutlineReader.Node item) return;
        int target = PdfOutlineReader.VisibleIndex(_plan.Pages, item.SourcePageIndex);
        if (target < 0)
        {
            StatusText.Text = "Переход недоступен: цель оглавления не поддерживается или удалена.";
            return;
        }
        _pageIndex = target;
        RefreshList();
    }

    private void PagePaneToggle_Click(object sender, RoutedEventArgs e)
    {
        _pagePaneOverride = !PdfWorkspaceLayout.ShowPages(ActualWidth, _pagePaneOverride);
        ApplyResponsiveLayout();
    }

    private void ShowPageListAndFocus()
    {
        _pagePaneOverride = true;
        ApplyResponsiveLayout();
        if (PageList.IsEnabled)
            PageList.Focus();
        else
            PagePaneToggleButton.Focus();
    }

    private void FocusNextWorkspaceRegion()
    {
        if (PdfToolModeBox.IsKeyboardFocusWithin)
        {
            if (PagePaneBorder.Visibility == Visibility.Visible && PageList.IsEnabled)
                PageList.Focus();
            else
                PdfPreviewScroll.Focus();
        }
        else if (PageList.IsKeyboardFocusWithin || PdfOutlineTree.IsKeyboardFocusWithin)
            PdfPreviewScroll.Focus();
        else if (PdfPreviewScroll.IsKeyboardFocusWithin && SaveAsButton.IsEnabled)
            SaveAsButton.Focus();
        else if (_editingToolsVisible)
            PdfToolModeBox.Focus();
        else
            SearchBox.Focus();
    }

    private async void Viewer_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsiveLayout();
        await EnsureInitialDocumentLoadedAsync();
    }

    // Background tabs may not have joined the visual tree. Explicit page
    // copy can load one on demand, without loading the same file twice.
    internal Task EnsureInitialDocumentLoadedAsync()
    {
        if (_plan is not null || _initialPath is null || _closing)
            return Task.CompletedTask;
        if (!_initializedWorkspace)
        {
            _initializedWorkspace = true;
            _initialLoadTask = LoadPdfAsync(_initialPath);
        }
        return _initialLoadTask ?? Task.CompletedTask;
    }

    private bool DiscardEditsIfNeeded()
    {
        return !HasPendingChanges || MessageBox.Show(HostWindow,
            _nativeDrafts.Count > 0
                ? "Эскизы PDF-аннотаций будут удалены — они не записываются в PDF. Продолжить?"
                : "Изменения не сохранены. Закрыть документ без сохранения?",
            "GET PDF · PDF", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:defaultapps")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("pdf-default-app-settings", ex);
            MessageBox.Show(HostWindow, "Откройте Параметры Windows → Приложения → Приложения по умолчанию " +
                "и выберите GET PDF для файлов .pdf.", "Приложение PDF по умолчанию");
        }
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _rendering || _closing) return;
        var dialog = new OpenFileDialog { Title = "Открыть PDF", Filter = "PDF (*.pdf)|*.pdf", Multiselect = true };
        if (dialog.ShowDialog(HostWindow) == true)
            await OpenPdfPathsAsync(dialog.FileNames);
    }

    private Task OpenPdfPathsAsync(IEnumerable<string> paths)
    {
        if (HostWindow is not PdfTabbedWindow shell)
            throw new InvalidOperationException("Не найдено окно вкладок ГЭТ PDF.");
        return shell.OpenPathsAsync(paths);
    }

    private void Viewer_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_loading && !_closing && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Viewer_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_closing || _loading || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        await OpenPdfPathsAsync(paths);
    }

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button owner ||
            !WorkstationPolicy.Current.RememberRecentDocuments) return;
        var menu = new ContextMenu();
        foreach (string path in PdfRecentDocuments.Read())
        {
            var item = new MenuItem { Header = Path.GetFileName(path), ToolTip = path };
            item.Click += async (_, _) =>
            {
                if (_closing || _loading) return;
                await OpenPdfPathsAsync([path]);
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Список пуст", IsEnabled = false });
        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "Очистить недавние" };
        clear.Click += (_, _) => PdfRecentDocuments.Clear();
        menu.Items.Add(clear);
        owner.ContextMenu = menu;
        menu.PlacementTarget = owner;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    internal async Task LoadPdfAsync(string path)
    {
        if (_loading) return;
        _loading = true;
        SetControls();
        StatusText.Text = "Открываем PDF…";
        try
        {
            if (!File.Exists(path) || !Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("PDF не найден или имеет неподдерживаемое расширение.");
            await StopThumbnailWorkerAsync();
            await DocumentView.StopAsync();
            ReleaseDocument();
            _originalPath = Path.GetFullPath(path);
            _sessionFolder = _scratchSession.Open();
            _sourcePath = Path.Combine(_sessionFolder, "source.pdf");
            _originalSourceCopyPath = _sourcePath;
            await PdfInputCopy.CopySafelyAsync(_originalPath, _sourcePath,
                WorkstationPolicy.Current.MaxPdfInputMiB * 1024L * 1024L);
            // The PDF viewer can start directly from Explorer without MainWindow.
            // Sweep old print-spool PNGs left by process or power failures.
            SessionStorage.CleanupExpired(TimeSpan.FromDays(2), _sessionFolder);
            _context = new ScanningContext(new WpfImageContext());
            _context.FileStorageManager = FileStorageManager.CreateFolder(_sessionFolder);
            _pageStore = new PdfLazyPageStore(_context, _sourcePath);
            _thumbnailCache = new PdfLruCache<(int, int), BitmapSource>(12, _ => { },
                48L * 1024 * 1024, EstimateBitmapBytes);
            if (_closing) return;
            if (_pageStore.PageCount > WorkstationPolicy.Current.MaxDocumentPages)
                throw new InvalidDataException("Превышен лимит страниц корпоративной политики.");
            _plan = new PdfPagePlan(_pageStore.PageCount);
            if (_documentSession.Pages.Count == _plan.Pages.Count)
                _plan.Restore(_plan.Pages.Select((p, i) => p with { Id = _documentSession.Pages[i].Id }).ToArray());
            ReadContinuousPageSizes();
            // Outline failures must not prevent viewing or printing a valid PDF.
            try
            {
                var outlineSource = _sourcePath;
                var navigation = await Task.Run(() =>
                {
                    using var pdf = PdfSharp.Pdf.IO.PdfReader.Open(outlineSource,
                        PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                    IReadOnlyList<PdfOutlineReader.Node> outline;
                    try { outline = PdfOutlineReader.Read(pdf); }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        DiagnosticLog.Failure("pdf-outline-read", ex);
                        outline = Array.Empty<PdfOutlineReader.Node>();
                    }
                    IReadOnlyDictionary<int, IReadOnlyList<PdfInternalLinkReader.Link>> links;
                    try { links = PdfInternalLinkReader.Read(pdf); }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        DiagnosticLog.Failure("pdf-internal-link-read", ex);
                        links = new Dictionary<int, IReadOnlyList<PdfInternalLinkReader.Link>>();
                    }
                    return (Outline: outline, Links: links);
                });
                _pdfOutline = navigation.Outline;
                _pdfInternalLinks = navigation.Links;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _pdfOutline = Array.Empty<PdfOutlineReader.Node>();
                _pdfInternalLinks = new Dictionary<int, IReadOnlyList<PdfInternalLinkReader.Link>>();
                DiagnosticLog.Failure("pdf-navigation-read", ex);
            }
            PdfOutlineTree.ItemsSource = _pdfOutline;
            UpdateOutlinePane();
            _history = new PdfEditHistory(_plan);
            _savedPlanFingerprint = null;
            try
            {
                PdfStructureGuard.EnsureSafeToRewrite(_sourcePath);
                _canModify = true;
                _markupAllowed = PdfMarkupPolicy.ReadEligibleSourcePages(_sourcePath);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Failure("pdf-structure-guard", ex);
                _canModify = false;
            }
            var viewState = _documentSession.ViewState;
            _pageIndex = Math.Max(0, _plan.Pages.ToList().FindIndex(p => p.Id == viewState.PageId));
            _zoom = viewState.Zoom;
            string? draftStatus = null;
            if (_canModify)
            {
                _draftSourceHash = await Task.Run(() => PdfDraftStore.HashSource(_sourcePath));
                draftStatus = RestorePdfDraftIfAvailable();
            }
            DocumentTitle.Text = _displayName ?? Path.GetFileName(path);
            RefreshList();
            DocumentView.RestoreViewport(_pageIndex, viewState.RelativeOffset, _zoom);
            DocumentReady?.Invoke();
            UpdateBookmarkButton();
            if (WorkstationPolicy.Current.RememberRecentDocuments)
            {
                try { PdfRecentDocuments.Remember(_originalPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { DiagnosticLog.Failure("pdf-recent", ex); }
            }
            StatusText.Text = draftStatus ?? (_canModify
                ? "Просмотр PDF. Исходный файл не изменён."
                : "Только просмотр: редактирование структуры документа не подтверждено.");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("pdf-viewer-load", ex);
            ReleaseDocument();
            _pdfOutline = Array.Empty<PdfOutlineReader.Node>();
            _pdfInternalLinks = new Dictionary<int, IReadOnlyList<PdfInternalLinkReader.Link>>();
            PdfOutlineTree.ItemsSource = null;
            DocumentTitle.Text = "Откройте документ";
            StatusText.Text = "Не удалось открыть PDF.";
            if (!_closing) MessageBox.Show(HostWindow, ex.Message, "Открытие PDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _loading = false;
            if (!_closing)
            {
                SetControls();
                EnsureThumbnailWorker();
            }
        }
    }

    private bool PersistPdfDraft()
    {
        if (_sessionFolder is null || _originalPath is null || _draftSourceHash is null ||
            _pageStore is null || _plan is null || !_canModify) return false;
        try
        {
            if (!HasPendingChanges) PdfDraftStore.Clear(_sessionFolder);
            else PdfDraftStore.Write(_sessionFolder, new PdfDraftStore.Manifest(
                _plan.SourcePageCount > _plan.OriginalPageCount ? 2 : 1, _originalPath, _draftSourceHash, _pageStore.PageCount, _plan.Snapshot(),
                Math.Clamp(_pageIndex, 0, _plan.Pages.Count - 1), _zoom,
                _plan.OriginalPageCount,
                _plan.SourcePageCount > _plan.OriginalPageCount ? Path.GetFileName(_sourcePath) : null,
                _plan.SourcePageCount > _plan.OriginalPageCount ? PdfDraftStore.HashSource(_sourcePath!) : null),
                WorkstationPolicy.Current.MaxDocumentPages);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Text.Json.JsonException or ArgumentException)
        {
            DiagnosticLog.Failure("pdf-draft-write", ex);
            StatusText.Text = "Не удалось обновить черновик PDF. Сохраните изменения вручную.";
            if (!_draftWarningShown)
            {
                _draftWarningShown = true;
                MessageBox.Show(HostWindow, "Не удалось обновить аварийный черновик PDF. " +
                    "Сохраните изменения в новый PDF перед закрытием программы.",
                    "Черновик PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return false;
        }
    }

    private string? RestorePdfDraftIfAvailable()
    {
        if (_originalPath is null || _draftSourceHash is null || _pageStore is null ||
            _plan is null || _history is null) return null;
        using var candidate = PdfDraftStore.FindLatest(_originalPath, WorkstationPolicy.Current.MaxDocumentPages);
        if (candidate is null) return null;
        if (!PdfDraftStore.MatchesSource(candidate.State, _draftSourceHash, _pageStore.PageCount))
        {
            MessageBox.Show(HostWindow, "Найден черновик, но исходный PDF изменился. Правки не применены. " +
                "Старый черновик сохранён для отдельного восстановления.", "Восстановление PDF",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return "PDF открыт без старых правок: исходник отличается от черновика.";
        }
        if (MessageBox.Show(HostWindow, "Найдены несохранённые правки этого PDF после прерванного сеанса. " +
            "Восстановить страницы, текст, повороты, штампы, рамки и заметки?",
            "Восстановление PDF", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return "PDF открыт без восстановления. Старый черновик сохранён.";
        try
        {
            var restored = PdfDraftStore.Validate(candidate.State, WorkstationPolicy.Current.MaxDocumentPages);
            if (candidate.WorkingSourcePath is { } working)
            {
                string destination = Path.Combine(_sessionFolder!, "text-master-" + Guid.NewGuid().ToString("N") + ".pdf");
                File.Copy(working, destination);
                _pageStore.Dispose(); _sourcePath = destination;
                _pageStore = new PdfLazyPageStore(_context!, destination);
                while (_plan.SourcePageCount < restored.SourcePageCount) _plan.RegisterSourcePage();
                _markupAllowed = PdfMarkupPolicy.ReadEligibleSourcePages(destination);
                ReadContinuousPageSizes();
            }
            foreach (var page in restored.Pages)
                if ((page.Rectangles is { Count: > 0 } || page.Notes is { Count: > 0 }) &&
                    (page.SourcePage >= _markupAllowed.Length || !_markupAllowed[page.SourcePage]))
                    throw new InvalidDataException("Разметка черновика несовместима с геометрией PDF.");
            _history.Execute(plan => plan.Restore(restored.Pages));
            _pageIndex = candidate.State.SelectedPage;
            _zoom = candidate.State.Zoom;
            // Consume the old session only after its replacement has been flushed
            // atomically. Failure leaves the original crash recovery untouched.
            if (PersistPdfDraft()) candidate.Consume();
            else return "Правки восстановлены; исходный черновик сохранён, новая резервная запись не удалась.";
            return "Правки PDF восстановлены. Ctrl+Z возвращает исходный порядок страниц.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            DiagnosticLog.Failure("pdf-draft-restore", ex);
            return "Черновик PDF не применён; исходный черновик сохранён.";
        }
    }

    private void ReleaseDocument()
    {
        _continuousFingerprint = null;
        PdfPageImage.Source = null;
        PdfSearchHighlightCanvas.Children.Clear();
        _searchGeometryCache.Clear();
        _thumbnailCancel.Cancel();
        foreach (var entry in _visibleThumbnails.ToArray())
            UntrackThumbnail(entry);
        _thumbnailItems = [];
        PageList.ItemsSource = null;
        ClearTextSelection(resetMode: true);
        _pdfOutline = Array.Empty<PdfOutlineReader.Node>();
        _pdfInternalLinks = new Dictionary<int, IReadOnlyList<PdfInternalLinkReader.Link>>();
        PdfOutlineTree.ItemsSource = null;
        _showOutline = false;
        UpdateOutlinePane();
        _thumbnailCache?.Dispose();
        _thumbnailCache = null;
        _dragStart = null;
        _dragPreview = null;
        _nativeDrafts.Discard();
        _nativeDraftMode = null;
        PdfMarkupCanvas.Children.Clear();
        _markupAllowed = Array.Empty<bool>();
        _pageStore?.Dispose();
        _pageStore = null;
        _canModify = false;
        _plan = null;
        _history = null;
        _renderRequests.Reset();
        _savedPlanFingerprint = null;
        _textIndex = null;
        _searchHits = Array.Empty<PdfTextHit>();
        _activeHit = 0;
        _activeSearchQuery = string.Empty;
        RefreshSearchResults();
        _sourcePath = null;
        _originalSourceCopyPath = null;
        _context?.Dispose();
        _context = null;
        _originalPath = null;
        _draftSourceHash = null;
        _draftWarningShown = false;
        _scratchSession.Close();
        _sessionFolder = null;
    }

    private void UpdateBookmarkButton()
    {
        if (BookmarkButton is null) return;
        BookmarkButton.Content = _originalPath is not null &&
            PdfPageBookmarks.Read(_originalPath).Contains(_pageIndex) ? "Закладка ★" : "Закладка ☆";
    }

    private void RefreshList()
    {
        if (_plan is null) return;
        _refreshingList = true;
        try
        {
            var references = _plan.Pages;
            bool unchanged = _thumbnailItems.Count == references.Count &&
                _thumbnailItems.Select((item, i) =>
                    item.Index == i && item.SourcePage == references[i].SourcePage &&
                    item.Rotation == references[i].Rotation &&
                    item.HasMarkup == (references[i].Stamp.Length > 0 ||
                        references[i].Rectangles is { Count: > 0 } ||
                        references[i].Notes is { Count: > 0 })).All(value => value);
            if (!unchanged)
            {
                // Page metadata is cheap; no PDF page is imported while building this list.
                _thumbnailCancel.Cancel();
                _thumbnailCancel.Dispose();
                _thumbnailCancel = new CancellationTokenSource();
                foreach (var entry in _visibleThumbnails.ToArray())
                    UntrackThumbnail(entry);
                _thumbnailItems = references.Select((page, index) =>
                    new PdfThumbnailItem(index, page)).ToArray();
                PageList.ItemsSource = _thumbnailItems;
            }
            _pageIndex = Math.Clamp(_pageIndex, 0, _plan.Pages.Count - 1);
            PageList.SelectedIndex = _pageIndex;
        }
        finally { _refreshingList = false; }
        if (_textIndex is not null && _activeSearchQuery.Length > 0)
        {
            _searchHits = _textIndex.Find(_plan.Pages, _activeSearchQuery);
            _activeHit = Math.Clamp(_activeHit, 0, Math.Max(0, _searchHits.Count - 1));
        }
        RefreshSearchResults();
        SynchronizePdfSession();
        ShowPage();
    }

    private void ShowPage()
    {
        if (_plan is null || _pageStore is null || _closing) return;
        ConfigureContinuousDocument();
        DocumentView.Zoom = _zoom;
        DocumentView.ScrollToPage(_pageIndex);
        PagePosition.Text = $"{_pageIndex + 1} / {_plan.Pages.Count}";
        if (_textSelectionMode) _ = EnsureSelectionWordsAsync();
        if (!PageNumberBox.IsKeyboardFocusWithin)
            PageNumberBox.Text = (_pageIndex + 1).ToString();
        UpdateBookmarkButton();
        if (_displayedPageIndex == _pageIndex && _displayedFingerprint == _continuousFingerprint && PdfPageImage.Source is BitmapSource bitmap)
            _ = RefreshPageSearchHighlightAsync(_pageIndex, _plan.Pages[_pageIndex], bitmap.Width, bitmap.Height);
        SetControls();
        if (ActiveToolIndex == 4) _ = LoadTextFragmentsAsync();
    }

    private async Task RefreshPageSearchHighlightAsync(
        int visibleIndex, PdfPageReference reference, double width, double height)
    {
        if (_sourcePath is null || _textIndex is null || _closing ||
            _activeSearchQuery.Length == 0 || _searchHits.Count == 0 ||
            _activeHit < 0 || _activeHit >= _searchHits.Count ||
            _searchHits[_activeHit].PageIndex != visibleIndex)
            return;
        string source = _sourcePath;
        string query = _activeSearchQuery;
        int requestedHit = _activeHit;
        var requestedPlan = _plan;
        var requestedIndex = _textIndex;
        if (requestedPlan is null) return;
        string sourceText = requestedIndex.GetSourceText(reference.SourcePage);
        if (sourceText.Length == 0) return;

        bool IsCurrentSearchHighlightRequest() =>
            !_closing && ReferenceEquals(_plan, requestedPlan) &&
            _sourcePath == source && ReferenceEquals(_textIndex, requestedIndex) &&
            _pageIndex == visibleIndex && _activeHit == requestedHit &&
            _activeSearchQuery == query && visibleIndex < requestedPlan.Pages.Count &&
            requestedPlan.Pages[visibleIndex] == reference;

        try
        {
            var cacheKey = (reference.SourcePage, query, reference.Rotation);
            if (!_searchGeometryCache.TryGetValue(cacheKey, out var rectangles))
            {
                rectangles = await Task.Run(() => PdfSearchGeometryReader.Find(
                    source, reference.SourcePage, query, sourceText, reference.Rotation));
                if (!IsCurrentSearchHighlightRequest()) return;
                if (_searchGeometryCache.Count >= 8) _searchGeometryCache.Clear();
                _searchGeometryCache[cacheKey] = rectangles;
            }

            if (!IsCurrentSearchHighlightRequest()) return;
            PdfSearchHighlightCanvas.Children.Clear();
            foreach (var bounds in rectangles)
            {
                var highlight = new Rectangle
                {
                    Width = bounds.Width * width,
                    Height = bounds.Height * height,
                    Fill = new SolidColorBrush(Color.FromArgb(85, 255, 190, 30)),
                    Stroke = new SolidColorBrush(Color.FromArgb(210, 190, 105, 12)),
                    StrokeThickness = 1,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(highlight, bounds.X * width);
                Canvas.SetTop(highlight, bounds.Y * height);
                PdfSearchHighlightCanvas.Children.Add(highlight);
            }
        }
        catch (Exception ex)
        {
            // Geometry is advisory. A malformed PDF or unsupported layout must
            // never break normal preview, editing, exporting or text search.
            DiagnosticLog.Failure("pdf-search-geometry", ex);
            if (IsCurrentSearchHighlightRequest())
                PdfSearchHighlightCanvas.Children.Clear();
        }
    }

    private void SetControls()
    {
        var page = _plan is not null && _pageIndex >= 0 && _pageIndex < _plan.Pages.Count
            ? _plan.Pages[_pageIndex] : null;
        _workspace.Update(new PdfWorkspaceState
        {
            PageCount = _plan?.Pages.Count ?? 0,
            PageIndex = _pageIndex,
            MaxDocumentPages = WorkstationPolicy.Current.MaxDocumentPages,
            IsLoading = _loading,
            IsRendering = _rendering,
            CanModify = _canModify,
            HasNativeDrafts = _nativeDrafts.Count > 0,
            IsNativeTextMode = _nativeDraftMode == "text",
            HasChanges = _plan?.HasChanges == true,
            CanUndo = _history?.CanUndo == true,
            CanRedo = _history?.CanRedo == true,
            CanDrawOnCurrentPage = CanDrawOnCurrentPage,
            HasRectangles = page?.Rectangles is { Count: > 0 },
            HasNotes = page?.Notes is { Count: > 0 },
            HasSearchHits = _searchHits.Count > 0,
            AllowPrinting = WorkstationPolicy.Current.AllowPrinting,
            IsJobActive = _batchCancel is not null,
            CanCancelJob = _batchCancel is { IsCancellationRequested: false }
        });
        CancelPdfJobButton.Visibility = _workspace.IsJobActive
            ? Visibility.Visible : Visibility.Collapsed;
        PdfJobProgressBar.Visibility = _workspace.IsJobActive
            ? Visibility.Visible : Visibility.Collapsed;
        var ready = _workspace.IsReady;
        bool hasNativeDrafts = _nativeDrafts.Count > 0;
        NativeDraftButton.IsEnabled = ready &&
            (hasNativeDrafts || (_canModify && CanDrawOnCurrentPage &&
             _plan!.Pages.Count <= PdfNativeAnnotationPolicy.MaxPdfPages));
        NativeDraftButton.Content = hasNativeDrafts
            ? $"Эскизы /Annots: {_nativeDrafts.Count} ▾" : "Аннотации · эскиз ▾";
        PdfStructureButton.IsEnabled = ready && _sourcePath is not null;
        // Temporarily disable markup tools while rendering without losing
        // the operator's chosen tool; reset only for intrinsically ineligible pages.
        if (_plan is null || !_canModify || !CanDrawOnCurrentPage || hasNativeDrafts)
        {
            RectangleModeCheckBox.IsChecked = false;
            NoteModeCheckBox.IsChecked = false;
        }
        if (_plan is null || !_canModify || !CanDrawOnCurrentPage)
            _nativeDraftMode = null;
    }

    private bool CanDrawOnCurrentPage =>
        _plan is not null && _pageIndex >= 0 && _pageIndex < _plan.Pages.Count &&
        _plan.Pages[_pageIndex].Rotation == 0 &&
        _plan.Pages[_pageIndex].SourcePage < _markupAllowed.Length &&
        _markupAllowed[_plan.Pages[_pageIndex].SourcePage];

    private void ShowRectangles(PdfPageReference reference)
    {
        PdfMarkupCanvas.Children.Clear();
        _dragStart = null;
        _dragPreview = null;
        foreach (var box in reference.Rectangles ?? Array.Empty<PdfRectangleMarkup>())
        {
            var shape = new Rectangle
            {
                Stroke = Brushes.Red,
                StrokeThickness = 2,
                Fill = Brushes.Transparent,
                IsHitTestVisible = false,
                Width = box.Width * PageVisual.Width,
                Height = box.Height * PageVisual.Height
            };
            Canvas.SetLeft(shape, box.X * PageVisual.Width);
            Canvas.SetTop(shape, box.Y * PageVisual.Height);
            PdfMarkupCanvas.Children.Add(shape);
        }
        foreach (var note in reference.Notes ?? Array.Empty<PdfTextNote>())
        {
            var block = new TextBlock
            {
                Text = note.Text, FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap,
                IsHitTestVisible = false
            };
            var border = new Border
            {
                Width = .4 * PageVisual.Width,
                Height = .12 * PageVisual.Height,
                BorderBrush = Brushes.DarkRed,
                BorderThickness = new Thickness(1),
                Background = Brushes.LightYellow,
                Padding = new Thickness(4),
                Child = block,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(border, note.X * PageVisual.Width);
            Canvas.SetTop(border, note.Y * PageVisual.Height);
            PdfMarkupCanvas.Children.Add(border);
        }
        // Native annotation preview is a separate transient overlay.
        // It is never part of the PDF SaveAs/PdfPagePlan writer.
        foreach (var draft in _nativeDrafts.ForPage(_pageIndex))
        {
            double x = draft.Left * PageVisual.Width;
            double y = draft.Top * PageVisual.Height;
            double w = (draft.Right - draft.Left) * PageVisual.Width;
            double h = (draft.Bottom - draft.Top) * PageVisual.Height;
            if (draft.Kind == "text")
            {
                var icon = new Border
                {
                    Width = Math.Max(10, w), Height = Math.Max(10, h),
                    BorderBrush = (Brush)FindResource("Navy"), BorderThickness = new Thickness(1),
                    Background = Brushes.LightGoldenrodYellow, CornerRadius = new CornerRadius(4),
                    IsHitTestVisible = false,
                    Child = new TextBlock
                    {
                        Text = "✎", TextAlignment = TextAlignment.Center,
                        FontSize = 14, FontWeight = FontWeights.Bold,
                        Foreground = (Brush)FindResource("Navy")
                    }
                };
                Canvas.SetLeft(icon, x);
                Canvas.SetTop(icon, y);
                PdfMarkupCanvas.Children.Add(icon);
            }
            else
            {
                var shape = new Rectangle
                {
                    Width = w, Height = h, StrokeThickness = 2,
                    Stroke = draft.Kind == "square" ? (Brush)FindResource("Navy") : Brushes.Goldenrod,
                    StrokeDashArray = new DoubleCollection { 3, 2 },
                    Fill = draft.Kind == "highlight"
                        ? new SolidColorBrush(Color.FromArgb(70, 255, 208, 0))
                        : Brushes.Transparent,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(shape, x);
                Canvas.SetTop(shape, y);
                PdfMarkupCanvas.Children.Add(shape);
            }
        }
    }

    // A single compact menu shares the existing PDF preview canvas and its zoom.
    // All operations are in-memory sketches only: no PDF /Annots are written by WPF.
    private void NativeDraftMenu_Click(object sender, RoutedEventArgs e)
    {
        if (NativeDraftButton.ContextMenu is not null)
        {
            NativeDraftButton.ContextMenu.PlacementTarget = NativeDraftButton;
            NativeDraftButton.ContextMenu.IsOpen = true;
        }
    }

    private void NativeDraftText_Click(object sender, RoutedEventArgs e) =>
        SelectNativeDraftMode("text");

    private void NativeDraftSquare_Click(object sender, RoutedEventArgs e) =>
        SelectNativeDraftMode("square");

    private void NativeDraftHighlight_Click(object sender, RoutedEventArgs e) =>
        SelectNativeDraftMode("highlight");

    private void SelectNativeDraftMode(string kind)
    {
        if (_plan is null || !_canModify || _loading || _rendering ||
            !CanDrawOnCurrentPage ||
            _plan.Pages.Count > PdfNativeAnnotationPolicy.MaxPdfPages) return;
        _nativeDraftMode = kind;
        RectangleModeCheckBox.IsChecked = false;
        NoteModeCheckBox.IsChecked = false;
        StatusText.Text = kind == "text"
            ? "Эскиз /Annots: щёлкните по странице. Текст берётся из поля заметки. НЕ сохраняется в PDF."
            : "Эскиз /Annots: выделите область мышью. НЕ сохраняется в PDF.";
        SetControls();
    }

    private void NativeDraftStop_Click(object sender, RoutedEventArgs e)
    {
        _nativeDraftMode = null;
        StatusText.Text = _nativeDrafts.Count > 0
            ? "Эскизы остаются на экране, но в PDF НЕ сохраняются. Для обычного сохранения очистите их."
            : "Предпросмотр аннотаций завершён; PDF не изменён.";
        SetControls();
    }

    private void NativeDraftUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_nativeDrafts.Undo())
        {
            if (_plan is not null) ShowRectangles(_plan.Pages[_pageIndex]);
            SetControls();
        }
        StatusText.Text = "Эскизы /Annots: отмена изменяет только предпросмотр, не PDF.";
    }

    private void NativeDraftRedo_Click(object sender, RoutedEventArgs e)
    {
        if (_nativeDrafts.Redo())
        {
            if (_plan is not null) ShowRectangles(_plan.Pages[_pageIndex]);
            SetControls();
        }
        StatusText.Text = "Эскизы /Annots: повтор изменяет только предпросмотр, не PDF.";
    }

    private void NativeDraftClear_Click(object sender, RoutedEventArgs e)
    {
        if (_nativeDrafts.Count > 0 &&
            MessageBox.Show(HostWindow,
                "Удалить все эскизы интерактивных аннотаций? В PDF они ещё не записаны.",
                "Очистить эскизы", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _nativeDrafts.Discard();
        _nativeDraftMode = null;
        if (_plan is not null) ShowRectangles(_plan.Pages[_pageIndex]);
        StatusText.Text = "Все эскизы PDF-аннотаций удалены из памяти. Исходник не изменён.";
        SetControls();
    }

    private void RectangleMode_Click(object sender, RoutedEventArgs e)
    {
        if (RectangleModeCheckBox.IsChecked == true)
        {
            NoteModeCheckBox.IsChecked = false;
            _nativeDraftMode = null;
        }
    }

    private void NoteMode_Click(object sender, RoutedEventArgs e)
    {
        if (NoteModeCheckBox.IsChecked == true)
        {
            RectangleModeCheckBox.IsChecked = false;
            _nativeDraftMode = null;
        }
    }

    private void PdfMarkupCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_textSelectionMode) { BeginTextSelection(e); return; }
        if (ActiveToolIndex == 4) { SelectTextAtPointer(e); return; }
        if (!CanDrawOnCurrentPage || _rendering || _loading || !_canModify ||
            PdfMarkupCanvas.ActualWidth < 1 || PdfMarkupCanvas.ActualHeight < 1)
            return;


        var point = e.GetPosition(PdfMarkupCanvas);
        if (_nativeDraftMode is "text" or "square" or "highlight")
        {
            if (_plan is null || _plan.Pages.Count > PdfNativeAnnotationPolicy.MaxPdfPages ||
                point.X < 0 || point.X > PdfMarkupCanvas.ActualWidth ||
                point.Y < 0 || point.Y > PdfMarkupCanvas.ActualHeight) return;
            try
            {
                if (_nativeDraftMode == "text")
                {
                    _nativeDrafts.Add(PdfNativeAnnotationDraft.FromClick(
                        _pageIndex, point.X, point.Y,
                        PdfMarkupCanvas.ActualWidth, PdfMarkupCanvas.ActualHeight,
                        NoteTextBox.Text));
                    ShowRectangles(_plan.Pages[_pageIndex]);
                    SetControls();
                    StatusText.Text = "Эскиз комментария добавлен. Не попадёт в PDF при сохранении.";
                }
                else
                {
                    _dragStart = point;
                    _dragPreview = new Rectangle
                    {
                        Stroke = (Brush)FindResource("Navy"),
                        StrokeThickness = 2,
                        StrokeDashArray = new DoubleCollection { 3, 2 },
                        Fill = _nativeDraftMode == "highlight"
                            ? new SolidColorBrush(Color.FromArgb(70, 255, 208, 0))
                            : Brushes.Transparent,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(_dragPreview, point.X);
                    Canvas.SetTop(_dragPreview, point.Y);
                    PdfMarkupCanvas.Children.Add(_dragPreview);
                    PdfMarkupCanvas.CaptureMouse();
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // Do not log user comment text.
                StatusText.Text = "Нельзя добавить эскиз: неверные данные или лимит 24 аннотации.";
            }
            e.Handled = true;
            return;
        }
        if (NoteModeCheckBox.IsChecked == true)
        {
            int selected = _pageIndex;
            var note = new PdfTextNote(
                Math.Clamp(point.X / PdfMarkupCanvas.ActualWidth, 0, .6),
                Math.Clamp(point.Y / PdfMarkupCanvas.ActualHeight, 0, .88),
                NoteTextBox.Text.Trim());
            ApplyEdit(p => p.AddNote(selected, note));
            StatusText.Text = "Заметка добавлена как видимая разметка. Для записи выберите «Сохранить как».";
            e.Handled = true;
            return;
        }
        if (RectangleModeCheckBox.IsChecked != true) return;
        if (point.X < 0 || point.Y < 0 || point.X > PdfMarkupCanvas.ActualWidth ||
            point.Y > PdfMarkupCanvas.ActualHeight) return;

        _dragStart = point;
        _dragPreview = new Rectangle
        {
            Stroke = Brushes.Red,
            StrokeThickness = 2,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(_dragPreview, point.X);
        Canvas.SetTop(_dragPreview, point.Y);
        PdfMarkupCanvas.Children.Add(_dragPreview);
        PdfMarkupCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void PdfMarkupCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_textSelectionDragging) { UpdateTextSelection(e); return; }
        if (_dragStart is not Point start || _dragPreview is null) return;
        var point = e.GetPosition(PdfMarkupCanvas);
        double x = Math.Clamp(point.X, 0, PdfMarkupCanvas.ActualWidth);
        double y = Math.Clamp(point.Y, 0, PdfMarkupCanvas.ActualHeight);
        Canvas.SetLeft(_dragPreview, Math.Min(start.X, x));
        Canvas.SetTop(_dragPreview, Math.Min(start.Y, y));
        _dragPreview.Width = Math.Abs(x - start.X);
        _dragPreview.Height = Math.Abs(y - start.Y);
        e.Handled = true;
    }

    private void PdfMarkupCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_textSelectionDragging) { FinishTextSelection(e); return; }
        if (_dragStart is not Point start) return;
        var finish = e.GetPosition(PdfMarkupCanvas);
        double width = PdfMarkupCanvas.ActualWidth;
        double height = PdfMarkupCanvas.ActualHeight;
        double x1 = Math.Clamp(start.X, 0, width);
        double x2 = Math.Clamp(finish.X, 0, width);
        double y1 = Math.Clamp(start.Y, 0, height);
        double y2 = Math.Clamp(finish.Y, 0, height);
        // Compute before releasing capture; LostMouseCapture resets the drag state.
        PdfMarkupCanvas.ReleaseMouseCapture();
        _dragStart = null;
        _dragPreview = null;
        if (_nativeDraftMode is "square" or "highlight")
        {
            if (_plan is not null && CanDrawOnCurrentPage &&
                _plan.Pages.Count <= PdfNativeAnnotationPolicy.MaxPdfPages)
            {
                try
                {
                    var draft = PdfNativeAnnotationDraft.FromDrag(
                        _nativeDraftMode, _pageIndex,
                        x1, y1, x2, y2, width, height);
                    if (draft is not null) _nativeDrafts.Add(draft);
                    ShowRectangles(_plan.Pages[_pageIndex]);
                    StatusText.Text = "Эскиз /Annots обновлён. PDF НЕ изменяется и не сохраняет эти эскизы.";
                    SetControls();
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    StatusText.Text = "Область эскиза не принята или достигнут лимит 24 аннотаций.";
                    ShowRectangles(_plan.Pages[_pageIndex]);
                }
            }
            e.Handled = true;
            return;
        }
        if (Math.Abs(x1 - x2) >= 6 && Math.Abs(y1 - y2) >= 6 &&
            width > 0 && height > 0 && CanDrawOnCurrentPage)
        {
            var shape = new PdfRectangleMarkup(
                Math.Min(x1, x2) / width,
                Math.Min(y1, y2) / height,
                Math.Abs(x2 - x1) / width,
                Math.Abs(y2 - y1) / height);
            int selected = _pageIndex;
            ApplyEdit(p => p.AddRectangle(selected, shape));
            StatusText.Text = "Рамка добавлена как векторная разметка. Сохраните отдельную PDF-копию.";
        }
        else if (_plan is not null) ShowRectangles(_plan.Pages[_pageIndex]);
        e.Handled = true;
    }

    private void PdfMarkupCanvas_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _textSelectionDragging = false;
        _dragStart = null;
        _dragPreview = null;
        if (_plan is not null && _pageIndex < _plan.Pages.Count)
            ShowRectangles(_plan.Pages[_pageIndex]);
    }

    private void ClearRectangles_Click(object sender, RoutedEventArgs e)
    {
        int selected = _pageIndex;
        if (CanDrawOnCurrentPage) ApplyEdit(p => p.ClearRectangles(selected));
    }

    private void ClearNotes_Click(object sender, RoutedEventArgs e)
    {
        int selected = _pageIndex;
        if (CanDrawOnCurrentPage) ApplyEdit(p => p.ClearNotes(selected));
    }

    private void PageThumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image { DataContext: PdfThumbnailItem item } &&
            PagePaneBorder.Visibility == Visibility.Visible)
            TrackThumbnail(item);
    }

    private void PageThumbnail_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image { DataContext: PdfThumbnailItem item })
            UntrackThumbnail(item);
    }

    private void PageThumbnail_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PdfThumbnailItem previous)
            UntrackThumbnail(previous);
        if (sender is Image { IsLoaded: true } &&
            PagePaneBorder.Visibility == Visibility.Visible &&
            e.NewValue is PdfThumbnailItem current)
            TrackThumbnail(current);
    }

    private void TrackThumbnail(PdfThumbnailItem item)
    {
        if (_pageStore is null || !_thumbnailItems.Contains(item)) return;
        _visibleThumbnails.Add(item);
        if (_thumbnailCache?.TryGet(item.CacheKey, out var cached) == true && cached is not null)
            item.Thumbnail = cached;
        else
            EnsureThumbnailWorker();
    }

    private void UntrackThumbnail(PdfThumbnailItem item)
    {
        _visibleThumbnails.Remove(item);
        item.Clear();
    }

    private void EnsureThumbnailWorker()
    {
        if (_closing || _loading || _rendering || _pageStore is null ||
            PagePaneBorder.Visibility != Visibility.Visible ||
            !_thumbnailWorker.IsCompleted ||
            !_visibleThumbnails.Any(item => item.Thumbnail is null && !item.Failed))
            return;
        _thumbnailWorker = DrainThumbnailQueueAsync(_thumbnailCancel.Token);
    }

    private async Task DrainThumbnailQueueAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && !_closing && !_loading &&
                   !_rendering && _pageStore is { } store)
            {
                var item = _visibleThumbnails.FirstOrDefault(entry =>
                    entry.Thumbnail is null && !entry.Failed && _thumbnailItems.Contains(entry));
                if (item is null) break;

                try
                {
                    if (_thumbnailCache?.TryGet(item.CacheKey, out var cached) == true &&
                        cached is not null)
                    {
                        item.Thumbnail = cached;
                        continue;
                    }
                    // Reuse the same PDFium importer/store; never prefetch hidden pages.
                    var page = await store.GetPageAsync(item.SourcePage, token);
                    if (token.IsCancellationRequested || _closing || !_visibleThumbnails.Contains(item) ||
                        !_thumbnailItems.Contains(item)) continue;
                    var bitmap = PdfThumbnailRenderer.Render(page, item.Rotation);
                    if (token.IsCancellationRequested || _closing || !_visibleThumbnails.Contains(item) ||
                        !_thumbnailItems.Contains(item)) continue;
                    _thumbnailCache?.Add(item.CacheKey, bitmap);
                    item.Thumbnail = bitmap;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (!token.IsCancellationRequested && _visibleThumbnails.Contains(item))
                        item.Failed = true;
                    DiagnosticLog.Failure("pdf-thumbnail", ex);
                }
            }
        }
        finally
        {
            // Schedule after this worker completes, so a new generation never
            // overlaps the old import in the same NAPS2/PDFium page store.
            if (!_closing)
                _ = Dispatcher.BeginInvoke(new Action(EnsureThumbnailWorker),
                    DispatcherPriority.Background);
        }
    }

    private async Task StopThumbnailWorkerAsync()
    {
        _thumbnailCancel.Cancel();
        try { await _thumbnailWorker; }
        catch (OperationCanceledException) { }
        _thumbnailCancel.Dispose();
        _thumbnailCancel = new CancellationTokenSource();
        foreach (var item in _visibleThumbnails.ToArray())
            UntrackThumbnail(item);
    }

    private void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingList || _loading || _plan is null || PageList.SelectedIndex < 0) return;
        _pageIndex = PageList.SelectedIndex;
        ShowPage();
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    { if (_workspace.CanNavigatePrevious) { _pageIndex--; RefreshList(); } }
    private void Next_Click(object sender, RoutedEventArgs e)
    { if (_workspace.CanNavigateNext) { _pageIndex++; RefreshList(); } }

    private void ApplyEdit(Action<PdfPagePlan> command, int selectedPageOffset = 0)
    {
        if (!_canModify || _plan is null || _history is null ||
            _loading || _rendering || _nativeDrafts.Count > 0) return;
        try
        {
            _history.Execute(command);
            _pageIndex += selectedPageOffset;
            PersistPdfDraft();
            RefreshList();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(HostWindow, ex.Message, "Редактирование PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RotateLeft_Click(object sender, RoutedEventArgs e) =>
        ApplyEdit(p => p.RotateCounterclockwise(_pageIndex));

    private void RotateRight_Click(object sender, RoutedEventArgs e) =>
        ApplyEdit(p => p.RotateClockwise(_pageIndex));

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MovePage(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MovePage(1);

    private void MovePage(int delta)
    {
        if (_plan is null || _pageIndex + delta < 0 || _pageIndex + delta >= _plan.Pages.Count) return;
        int from = _pageIndex;
        ApplyEdit(p => p.Move(from, from + delta), delta);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null || _plan.Pages.Count == 1) return;
        int page = _pageIndex;
        ApplyEdit(p => p.Delete(page));
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null || _plan.Pages.Count >= WorkstationPolicy.Current.MaxDocumentPages) return;
        int page = _pageIndex;
        ApplyEdit(p => p.Duplicate(page), selectedPageOffset: 1);
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_nativeDrafts.Count > 0 || !_workspace.CanUndo || _history is null) return;
        if (_history.Undo()) { PersistPdfDraft(); RefreshList(); }
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_nativeDrafts.Count > 0 || !_workspace.CanRedo || _history is null) return;
        if (_history.Redo()) { PersistPdfDraft(); RefreshList(); }
    }

    private void FitPage_Click(object sender, RoutedEventArgs e) => FitPreview(true);
    private void FitWidth_Click(object sender, RoutedEventArgs e) => FitPreview(false);
    private void PreviewScroll_SizeChanged(object sender, SizeChangedEventArgs e) { }
    private void FitPreview(bool wholePage)
    {
        if (PdfPageImage.Source is not BitmapSource bitmap) return;
        double width = Math.Max(1, PdfPreviewScroll.ViewportWidth - 24);
        double height = Math.Max(1, PdfPreviewScroll.ViewportHeight - 24);
        if (!wholePage) { DocumentView.FitWidth(); _zoom = DocumentView.Zoom; return; }
        var size = CurrentContinuousSize();
        _zoom = Math.Clamp(Math.Min(width / size.Width, height / size.Height), .15, 4);
        ShowPage();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    { if (!_workspace.CanZoom) return; _zoom = Math.Min(3, _zoom + .25); ShowPage(); }
    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    { if (!_workspace.CanZoom) return; _zoom = Math.Max(.5, _zoom - .25); ShowPage(); }
    private void Viewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || _plan is null) return;
        if (e.Delta > 0) ZoomIn_Click(sender, new RoutedEventArgs());
        else ZoomOut_Click(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private void Viewer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase && !_loading)
        {
            if (e.Key == Key.PageDown) { PdfPreviewScroll.PageDown(); e.Handled = true; return; }
            if (e.Key == Key.PageUp) { PdfPreviewScroll.PageUp(); e.Handled = true; return; }
        }
        bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (control && e.Key == Key.C &&
            Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase &&
            _textSelectionMode && CopySelectedTextButton.IsEnabled)
        {
            CopySelectedText_Click(sender, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (e.Key == Key.F6)
        {
            FocusNextWorkspaceRegion();
            e.Handled = true;
            return;
        }
        if (control && shift && e.Key == Key.L)
        {
            PagePaneToggle_Click(sender, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (control && !shift && e.Key == Key.G)
        {
            PageNumberBox.Focus();
            PageNumberBox.SelectAll();
            e.Handled = true;
            return;
        }
        if (control && !shift && e.Key is Key.D1 or Key.D2 or Key.D3 or Key.D4 or Key.D5)
        {
            PdfToolModeBox.SelectedIndex = e.Key switch
            {
                Key.D1 => 0, Key.D2 => 1, Key.D3 => 2, Key.D4 => 3, _ => 4
            };
            PdfToolModeBox.Focus();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F11) { Fullscreen_Click(sender, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key == Key.Escape && HostWindow is PdfTabbedWindow { IsFullscreen: true })
        { Fullscreen_Click(sender, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key == Key.Escape && _batchCancel is { IsCancellationRequested: false })
        {
            CancelPdfJob_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (control && e.Key == Key.O) { Open_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (control && e.Key == Key.S) { SaveAs_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (control && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F3)
        {
            if (shift) FindPrevious_Click(sender, new RoutedEventArgs());
            else FindNext_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (control && e.Key == Key.P) { Print_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (control && e.Key == Key.Z && Keyboard.FocusedElement is not
            System.Windows.Controls.Primitives.TextBoxBase)
        {
            Undo_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (control && e.Key == Key.Y && Keyboard.FocusedElement is not
            System.Windows.Controls.Primitives.TextBoxBase)
        {
            Redo_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }


    private void RefreshSearchResults()
    {
        _refreshingSearchResults = true;
        try
        {
            SearchResultsList.ItemsSource = null;
            SearchResultsList.ItemsSource = _searchHits;
            SearchResultsPanel.Visibility = _searchHits.Count > 0 && !_showOutline
                ? Visibility.Visible : Visibility.Collapsed;
            SearchResultsHeader.Text = _searchHits.Count >= PdfTextIndex.MaxResults
                ? $"Совпадения (первые {PdfTextIndex.MaxResults})"
                : $"Совпадений: {_searchHits.Count}";
            if (_searchHits.Count > 0)
                SearchResultsList.SelectedIndex = Math.Clamp(_activeHit, 0, _searchHits.Count - 1);
        }
        finally { _refreshingSearchResults = false; }
        ApplyResponsiveLayout();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || string.Equals(SearchBox.Text.Trim(), _activeSearchQuery,
                StringComparison.Ordinal)) return;
        // A changed query must not leave hits from the previous query on screen.
        _activeSearchQuery = string.Empty;
        _searchHits = Array.Empty<PdfTextHit>();
        _activeHit = 0;
        _searchGeometryCache.Clear();
        PdfSearchHighlightCanvas.Children.Clear();
        RefreshSearchResults();
        SetControls();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Find_Click(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingSearchResults || _loading || _plan is null ||
            SearchResultsList.SelectedIndex < 0 ||
            SearchResultsList.SelectedIndex == _activeHit) return;
        _activeHit = SearchResultsList.SelectedIndex;
        NavigateToHit();
    }

    // Inspection only: reads the private source copy and does not alter PDF content.
    private async void PdfStructure_Click(object sender, RoutedEventArgs e)
    {
        if (_sourcePath is null || _plan is null || _loading || _rendering) return;
        string source = _sourcePath;
        _loading = true;
        SetControls();
        StatusText.Text = "Проверяем структуру PDF без изменения файла…";
        try
        {
            var report = await Task.Run(() =>
            {
                using var pdf = PdfSharp.Pdf.IO.PdfReader.Open(
                    source, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                var structure = PdfInteractiveInspector.Inspect(pdf);
                var fields = PdfFormPreflight.Inspect(pdf, structure);
                return structure.ToRussianSummary() + Environment.NewLine +
                    Environment.NewLine + fields.ToRussianSummary();
            });
            StatusText.Text = "Проверка состава PDF завершена. Файл не изменён.";
            MessageBox.Show(HostWindow, report, "Состав PDF — только чтение",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("pdf-structure-inspect", ex);
            StatusText.Text = "Не удалось достоверно прочитать структуру PDF.";
            MessageBox.Show(HostWindow,
                "Структура PDF не распознана или слишком сложна для ограниченной проверки. " +
                "Это не подтверждает отсутствие подписей или аннотаций. Оригинал не изменён.",
                "Состав PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _loading = false; if (!_closing) SetControls(); }
    }

    private async void Find_Click(object sender, RoutedEventArgs e)
    {
        if (_sourcePath is null || _plan is null || _loading) return;
        string query = SearchBox.Text.Trim();
        if (query.Length == 0 || query.Length > PdfTextIndex.MaxQueryLength)
        {
            _activeSearchQuery = string.Empty;
            _searchHits = Array.Empty<PdfTextHit>();
            _activeHit = 0;
            RefreshSearchResults();
            StatusText.Text = query.Length == 0
                ? "Введите слово или фразу для поиска."
                : $"Запрос не должен превышать {PdfTextIndex.MaxQueryLength} символов.";
            SetControls();
            return;
        }
        _loading = true;
        SetControls();
        StatusText.Text = "Поиск в текстовом слое PDF…";
        try
        {
            string source = _sourcePath;
            _textIndex ??= await Task.Run(() => PdfTextReader.Build(source));
            var textIndex = _textIndex;
            var visiblePages = _plan.Pages.ToArray();
            _activeSearchQuery = query;
            _searchHits = await Task.Run(() => textIndex.Find(visiblePages, query));
            _activeHit = 0;
            if (_searchHits.Count > 0)
            {
                _showOutline = false;
                _pagePaneOverride = true;
                UpdateOutlinePane();
            }
            if (_searchHits.Count == 0)
            {
                RefreshSearchResults();
                StatusText.Text = "Совпадений нет. Для скана без текстового слоя сначала выполните OCR.";
            }
            else
                NavigateToHit();
        }
        catch (Exception ex)
        {
            _activeSearchQuery = string.Empty;
            _searchHits = Array.Empty<PdfTextHit>();
            _activeHit = 0;
            RefreshSearchResults();
            DiagnosticLog.Failure("pdf-text-search", ex);
            StatusText.Text = "Поиск недоступен для этого PDF.";
            MessageBox.Show(HostWindow, ex.Message, "Поиск в PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _loading = false; SetControls(); }
    }

    private void NavigateToHit()
    {
        if (_plan is null || _searchHits.Count == 0) return;
        _activeHit = Math.Clamp(_activeHit, 0, _searchHits.Count - 1);
        _pageIndex = _searchHits[_activeHit].PageIndex;
        _refreshingSearchResults = true;
        try
        {
            SearchResultsList.SelectedIndex = _activeHit;
            SearchResultsList.ScrollIntoView(_searchHits[_activeHit]);
        }
        finally { _refreshingSearchResults = false; }
        RefreshList();
        StatusText.Text = $"Совпадение {_activeHit + 1} / {_searchHits.Count}: " +
            _searchHits[_activeHit].Snippet;
    }

    private void FindNext_Click(object sender, RoutedEventArgs e)
    {
        if (_searchHits.Count == 0 || _loading) return;
        _activeHit = (_activeHit + 1) % _searchHits.Count;
        NavigateToHit();
    }

    private void FindPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (_searchHits.Count == 0 || _loading) return;
        _activeHit = PdfReaderNavigation.PreviousSearchHit(_activeHit, _searchHits.Count);
        NavigateToHit();
    }

    private void GoToPage_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _closing || _plan is null) return;
        if (!PdfReaderNavigation.TryParsePage(PageNumberBox.Text, _plan.Pages.Count,
                out int target))
        {
            StatusText.Text = $"Введите номер страницы от 1 до {_plan.Pages.Count}.";
            return;
        }
        _pageIndex = target;
        RefreshList();
        PdfPreviewScroll.Focus();
    }

    private void PageNumberBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        GoToPage_Click(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private void Stamp_Click(object sender, RoutedEventArgs e)
    {
        int page = _pageIndex;
        ApplyEdit(p => p.SetStamp(page, StampTextBox.Text));
        if (_canModify) StatusText.Text = "Штамп добавлен. Для записи выберите «Сохранить как».";
    }

    private static SaveFileDialog NewPdfOutputDialog(string name) => new()
    {
        Title = "Сохранить PDF в новый файл", Filter = "PDF (*.pdf)|*.pdf",
        DefaultExt = ".pdf", AddExtension = true, FileName = name + ".pdf", OverwritePrompt = true
    };

    private bool ValidateOutput(string destination, IEnumerable<string>? additional = null)
    {
        var inputs = new[] { _originalPath!, _sourcePath! }.Concat(additional ?? Array.Empty<string>());
        if (PdfSaveSafety.TryValidateNewCopy(destination, inputs, out var message)) return true;
        MessageBox.Show(HostWindow, message, "Сохранение PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private bool ConfirmStructuralExport() =>
        MessageBox.Show(HostWindow,
            "Действующие цифровые подписи, формы, закладки и интерактивные элементы могут " +
            "не сохраниться. Не редактируйте подписанные документы. Продолжить создание отдельной копии?",
            "Проверка PDF", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (_sourcePath is null || _plan is null || _loading) return;
        var dialog = NewPdfOutputDialog(Path.GetFileNameWithoutExtension(_originalPath) + "_страница_" + (_pageIndex + 1));
        if (dialog.ShowDialog(HostWindow) != true || !ValidateOutput(dialog.FileName) || !ConfirmStructuralExport()) return;
        var page = _plan.Pages[_pageIndex];
        _loading = true;
        SetControls();
        try
        {
            await RunPdfFileJobAsync(token => Task.Run(() => PdfDocumentWriter.SaveAs(_sourcePath, dialog.FileName, [page], token), token));
            StatusText.Text = await PostExportStatusAsync(dialog.FileName, "Страница извлечена, оригинал не изменён.");
        }
        catch (OperationCanceledException) { StatusText.Text = "Операция PDF отменена."; }
        catch (Exception ex) { DiagnosticLog.Failure("pdf-extract", ex); MessageBox.Show(HostWindow, ex.Message, "Извлечение PDF"); }
        finally { _loading = false; SetControls(); }
    }

    private async void ExtractRange_Click(object sender, RoutedEventArgs e)
    {
        if (_sourcePath is null || _plan is null || !_canModify || _loading || _rendering) return;
        IReadOnlyList<PdfPageReference> pages;
        try
        {
            pages = PdfPageRange.Parse(PageRangeBox.Text, _plan.Pages);
        }
        catch (FormatException ex)
        {
            MessageBox.Show(HostWindow, ex.Message, "Диапазон PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dialog = NewPdfOutputDialog(Path.GetFileNameWithoutExtension(_originalPath) + "_выдержка");
        if (dialog.ShowDialog(HostWindow) != true || !ValidateOutput(dialog.FileName) || !ConfirmStructuralExport()) return;
        string sourcePath = _sourcePath;
        _loading = true;
        SetControls();
        StatusText.Text = $"Извлечение {pages.Count} страниц без растрирования…";
        try
        {
            await RunPdfFileJobAsync(token => Task.Run(() => PdfDocumentWriter.SaveAs(sourcePath, dialog.FileName, pages, token), token));
            StatusText.Text = await PostExportStatusAsync(dialog.FileName,
                $"Выдержка из {pages.Count} страниц сохранена отдельно. Оригинал не изменён.");
        }
        catch (OperationCanceledException) { StatusText.Text = "Операция PDF отменена; результат не опубликован."; }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("pdf-extract-range", ex);
            MessageBox.Show(HostWindow, ex.Message, "Извлечение диапазона", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _loading = false; SetControls(); }
    }

    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (_sourcePath is null || _plan is null || _loading) return;
        var sources = new OpenFileDialog
        {
            Title = "Выберите PDF для добавления в конец",
            Filter = "PDF (*.pdf)|*.pdf", Multiselect = true
        };
        if (sources.ShowDialog(HostWindow) != true || sources.FileNames.Length == 0) return;
        var dialog = NewPdfOutputDialog(Path.GetFileNameWithoutExtension(_originalPath) + "_объединённый");
        if (dialog.ShowDialog(HostWindow) != true || !ValidateOutput(dialog.FileName) || !ConfirmStructuralExport()) return;
        var pages = _plan.Pages.ToArray();
        var originals = sources.FileNames.ToArray();
        _loading = true;
        SetControls();
        StatusText.Text = "Объединение PDF без растрирования…";
        try
        {
            await RunPdfFileJobAsync(token => Task.Run(() => PdfDocumentWriter.Merge(_sourcePath, pages, originals, dialog.FileName, token), token));
            StatusText.Text = await PostExportStatusAsync(dialog.FileName, "PDF объединены, исходные файлы не изменены.");
        }
        catch (OperationCanceledException) { StatusText.Text = "Операция PDF отменена."; }
        catch (Exception ex) { DiagnosticLog.Failure("pdf-merge", ex); MessageBox.Show(HostWindow, ex.Message, "Объединение PDF"); }
        finally { _loading = false; SetControls(); }
    }

    private async Task RunPdfFileJobAsync(Func<CancellationToken, Task> work)
    {
        using var cancellation = new CancellationTokenSource();
        _batchCancel = cancellation;
        _jobProgress.Begin();
        SetControls();
        StatusText.Text = "Ожидание очереди обработки PDF…";
        try
        {
            await PdfJobCoordinator.Shared.RunAsync(async token =>
            {
                _jobProgress.Start();
                StatusText.Text = "Обработка PDF…";
                await work(token);
                return true;
            }, cancellation.Token);
            _jobProgress.Complete();
        }
        catch (OperationCanceledException) { _jobProgress.Cancelled(); throw; }
        catch { _jobProgress.Fail(); throw; }
        finally { _batchCancel = null; SetControls(); }
    }

    private void CancelPdfJob_Click(object sender, RoutedEventArgs e)
    {
        if (_batchCancel is null || _batchCancel.IsCancellationRequested) return;
        _batchCancel.Cancel();
        _jobProgress.RequestCancellation();
        SetControls(); // Keep the command binding authoritative after cancellation.
        StatusText.Text = "Запрошена отмена. Активная операция завершит текущий этап безопасно…";
    }

    private void PreflightBatch(IReadOnlyList<int> sourceIndices, CancellationToken token)
    {
        if (_sourcePath is null) throw new InvalidOperationException("Нет открытого PDF.");
        token.ThrowIfCancellationRequested();
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(_sourcePath);
        var dimensions = new List<PdfPageDimensions>(sourceIndices.Count);
        foreach (int sourceIndex in sourceIndices)
        {
            token.ThrowIfCancellationRequested();
            if (sourceIndex < 0 || sourceIndex >= pdf.NumberOfPages)
                throw new InvalidDataException("Указана несуществующая страница PDF.");
            var page = pdf.GetPage(sourceIndex + 1);
            dimensions.Add(new PdfPageDimensions(page.Width, page.Height));
        }
        long estimated = PdfBatchSafety.EstimateRasterBytes(dimensions);
        PdfBatchSafety.EnsureWithinBudget(estimated, PdfBatchSafety.EffectiveBudget());
    }

    private Task<List<ProcessedImage>> LoadAllForBatchAsync(CancellationToken token)
    {
        if (_plan is null) throw new InvalidOperationException("Нет открытого PDF.");
        return LoadSelectedForBatchAsync(PdfBatchSelection.Build(_plan.Pages), token);
    }

    private async Task<List<ProcessedImage>> LoadSelectedForBatchAsync(
        PdfBatchSelection selection, CancellationToken token)
    {
        if (_context is null || _sourcePath is null)
            throw new InvalidOperationException("Нет открытого PDF.");
        // Only allocate raster pages actually needed by this operation.
        PreflightBatch(selection.SourcePages, token);
        var result = new List<ProcessedImage>(selection.SourcePages.Count);
        try
        {
            var importer = new PdfImporter(_context);
            foreach (int sourceIndex in selection.SourcePages)
            {
                token.ThrowIfCancellationRequested();
                ProcessedImage? single = null;
                try
                {
                    await foreach (var image in importer.Import(_sourcePath,
                        new ImportParams { Slice = Slice.Item(sourceIndex) }))
                    {
                        if (single is not null)
                        {
                            image.Dispose();
                            throw new InvalidDataException(
                                "Выборочный импорт PDF вернул больше одной страницы.");
                        }
                        single = image;
                    }
                    token.ThrowIfCancellationRequested();
                    if (single is null)
                        throw new InvalidDataException("Не удалось получить страницу PDF.");
                    result.Add(single);
                    single = null; // result takes ownership.
                }
                finally { single?.Dispose(); }
                StatusText.Text = $"Подготовка PDF: {result.Count} / {selection.SourcePages.Count} страниц…";
            }
            return result;
        }
        catch
        {
            foreach (var image in result) image.Dispose();
            throw;
        }
    }

    private async Task StagePrintPagesAsync(
        PdfBatchSelection selection, PdfPrintSpool spool, CancellationToken token)
    {
        if (_context is null || _sourcePath is null)
            throw new InvalidOperationException("Нет открытого PDF.");

        // Before printing, validate the largest *single* raster instead of
        // requiring enough RAM for every page simultaneously. Disk storage
        // has its own budget inside PdfPrintSpool.
        using (var pdf = UglyToad.PdfPig.PdfDocument.Open(_sourcePath))
        {
            long memoryBudget = PdfBatchSafety.EffectiveBudget();
            foreach (int index in selection.SourcePages)
            {
                token.ThrowIfCancellationRequested();
                if (index < 0 || index >= pdf.NumberOfPages)
                    throw new InvalidDataException("Выбранная PDF-страница не найдена.");
                var page = pdf.GetPage(index + 1);
                long estimate = PdfBatchSafety.EstimateRasterBytes(
                    [new PdfPageDimensions(page.Width, page.Height)]);
                PdfBatchSafety.EnsureWithinBudget(estimate, memoryBudget);
            }
        }

        var importer = new PdfImporter(_context);
        foreach (int sourceIndex in selection.SourcePages)
        {
            token.ThrowIfCancellationRequested();
            ProcessedImage? single = null;
            try
            {
                await foreach (var image in importer.Import(_sourcePath,
                    new ImportParams { Slice = Slice.Item(sourceIndex) })
                    .WithCancellation(token))
                {
                    if (token.IsCancellationRequested)
                    {
                        image.Dispose();
                        token.ThrowIfCancellationRequested();
                    }
                    if (single is not null)
                    {
                        image.Dispose();
                        throw new InvalidDataException(
                            "Выборочный импорт PDF вернул больше одной страницы.");
                    }
                    single = image;
                }

                token.ThrowIfCancellationRequested();
                if (single is null)
                    throw new InvalidDataException("Не удалось получить страницу PDF.");

                // This snapshot creates an independent frozen BitmapSource,
                // serializes PNG to local disk, and closes its stream. No
                // ProcessedImage is retained across page iterations.
                var file = await RecoverySession.SnapshotAsync(
                    spool.DirectoryPath, single.RenderToBitmapSource());
                spool.AddFile(file);
                _jobProgress.Report(spool.Pages.Count, selection.SourcePages.Count);
                StatusText.Text = $"Подготовлено к печати: {spool.Pages.Count} / " +
                    $"{selection.SourcePages.Count} исходных страниц…";
            }
            finally { single?.Dispose(); }
        }
        token.ThrowIfCancellationRequested();
    }

    private async void Ocr_Click(object sender, RoutedEventArgs e)
    {
        if (_context is null || _sourcePath is null || _plan is null ||
            _loading || _rendering || !_canModify) return;
        if (_plan.HasChanges)
        {
            MessageBox.Show(HostWindow,
                "OCR отдельного PDF не применяет несохранённые повороты, заметки и штампы. " +
                "Сначала сохраните новую копию и откройте её для OCR.",
                "OCR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var tessdata = Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (!File.Exists(Path.Combine(tessdata, "rus.traineddata")) ||
            !File.Exists(Path.Combine(tessdata, "eng.traineddata")))
        {
            MessageBox.Show(HostWindow, "Для OCR ИТ-служба должна установить одобренные offline модели " +
                "rus.traineddata и eng.traineddata в папку tessdata.", "OCR",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = NewPdfOutputDialog(Path.GetFileNameWithoutExtension(_originalPath) + "_OCR");
        if (dialog.ShowDialog(HostWindow) != true || !ValidateOutput(dialog.FileName) || !ConfirmStructuralExport()) return;
        var sourcePath = Path.GetFullPath(_sourcePath);
        var destination = Path.GetFullPath(dialog.FileName);
        if (string.Equals(destination, sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(HostWindow, "OCR должен сохраняться в отдельный PDF. Оригинал не перезаписывается.");
            return;
        }

        var sessionFolder = _sessionFolder;
        if (sessionFolder is null) return;
        var context = _context;
        var plannedPages = _plan.Pages.ToArray();
        int expectedPageCount = plannedPages.Length;

        using var cancellation = new CancellationTokenSource();
        _batchCancel = cancellation;
        _jobProgress.Begin();
        _loading = true;
        SetControls();
        StatusText.Text = "Очередь OCR · подготовка небольших пакетов…";

        try
        {
            await PdfJobCoordinator.Shared.RunAsync(async token =>
            {
                _jobProgress.Start();
                // Read geometry before raster import. The service below owns
                // scratch files, source identity checks and final publication.
                token.ThrowIfCancellationRequested();
                List<PdfPageDimensions> dimensions;
                using (var pdf = UglyToad.PdfPig.PdfDocument.Open(sourcePath))
                {
                    if (pdf.NumberOfPages != expectedPageCount)
                        throw new InvalidDataException("Число страниц исходного PDF изменилось.");

                    dimensions = new List<PdfPageDimensions>(pdf.NumberOfPages);
                    for (int i = 1; i <= pdf.NumberOfPages; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var page = pdf.GetPage(i);
                        dimensions.Add(new PdfPageDimensions(page.Width, page.Height));
                    }
                }

                long budget = Math.Min(PdfBatchSafety.EffectiveBudget(),
                    PdfOcrBatchPlanner.DefaultMaxRasterBytes);
                var batches = PdfOcrBatchPlanner.Plan(dimensions,
                    PdfOcrBatchPlanner.DefaultMaxPages, budget);
                context.OcrEngine = TesseractOcrEngine.Bundled(tessdata);

                await PdfOcrJobService.RunAsync(
                    sessionFolder, sourcePath, destination, batches,
                    async (batch, chunkPath, batchToken) =>
                    {
                        var sourceSelection = plannedPages
                            .Skip(batch.FirstSourcePage).Take(batch.PageCount).ToArray();
                        var selection = PdfBatchSelection.Build(sourceSelection);
                        List<ProcessedImage>? rasters = null;
                        try
                        {
                            StatusText.Text =
                                $"OCR: страницы {batch.FirstSourcePage + 1}–" +
                                $"{batch.FirstSourcePage + batch.PageCount}…";
                            rasters = await LoadSelectedForBatchAsync(selection, batchToken);
                            batchToken.ThrowIfCancellationRequested();
                            var exporter = new PdfExporter(context);
                            if (!await exporter.Export(chunkPath, rasters,
                                    ocrParams: new OcrParams("rus+eng")))
                                throw new IOException("OCR не завершил обработку пакета.");
                        }
                        finally
                        {
                            if (rasters is not null)
                                foreach (var raster in rasters) raster.Dispose();
                        }
                    },
                    (completed, total) =>
                    {
                        // Reserve final 10% for verifying and publishing the PDF.
                        _jobProgress.Report(completed, total, 90);
                        if (completed == total) _jobProgress.BeginFinalizing();
                        StatusText.Text = completed == total
                            ? "OCR завершён. Проверяем и сохраняем итоговый PDF…"
                            : $"OCR: готово пакетов {completed} / {total}.";
                    },
                    token);
                return true;
            }, cancellation.Token);

            _jobProgress.Complete();
            StatusText.Text = await PostExportStatusAsync(destination,
                $"Создан PDF с OCR для {expectedPageCount} страниц. Оригинал не изменён.");
        }
        catch (OperationCanceledException)
        {
            _jobProgress.Cancelled();
            StatusText.Text = "OCR отменён. Оригинальный PDF не изменён.";
        }
        catch (PdfOcrInsufficientSpaceException ex)
        {
            _jobProgress.Fail();
            StatusText.Text = "Недостаточно места на диске. OCR не запущен.";
            MessageBox.Show(HostWindow, ex.Message, "Недостаточно места для OCR",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _jobProgress.Fail();
            DiagnosticLog.Failure("pdf-ocr", ex);
            StatusText.Text = "Ошибка OCR PDF.";
            MessageBox.Show(HostWindow, ex.Message, "OCR", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _batchCancel = null;
            _loading = false;
            SetControls();
        }
    }

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        if (!_workspace.CanPrint || _plan is null) return;
        await PrintPagesAsync(_plan.Pages.ToArray());
    }

    private async void PrintRange_Click(object sender, RoutedEventArgs e)
    {
        if (!_workspace.CanPrint || _plan is null) return;
        IReadOnlyList<PdfPageReference> pages;
        try { pages = PdfPageRange.Parse(PrintPageRangeBox.Text, _plan.Pages); }
        catch (FormatException ex)
        {
            MessageBox.Show(HostWindow, ex.Message, "Печать диапазона", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        await PrintPagesAsync(pages);
    }

    private async Task PrintPagesAsync(IReadOnlyList<PdfPageReference> pages)
    {
        if (!_workspace.CanPrint || !WorkstationPolicy.Current.AllowPrinting || _plan is null) return;
        var printer = new PrintDialog();
        if (printer.ShowDialog() != true) return;
        var settingsDialog = new PdfPrintSettingsDialog(PdfPrinterSettingsStore.Read())
        { Owner = HostWindow };
        if (settingsDialog.ShowDialog() != true) return;
        try
        {
            PdfPrinterSettingsStore.ApplyTo(printer, settingsDialog.Settings);
            try { PdfPrinterSettingsStore.Save(settingsDialog.Settings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { DiagnosticLog.Failure("pdf-print-settings", ex); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Failure("pdf-printer-options", ex);
            MessageBox.Show(HostWindow,
                "Драйвер отклонил выбранные параметры. Проверьте устройство и его настройки. " +
                ex.Message,
                "Неподдерживаемые параметры принтера",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        PdfBatchSelection selection;
        try { selection = PdfBatchSelection.Build(pages); }
        catch (Exception ex)
        {
            MessageBox.Show(HostWindow, ex.Message, "Печать PDF", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _batchCancel = cancellation;
        _jobProgress.Begin();
        _loading = true;
        SetControls();
        StatusText.Text = $"Очередь печати PDF: {pages.Count} страниц…";
        try
        {
            bool submitted = await PdfJobCoordinator.Shared.RunAsync(async token =>
            {
                _jobProgress.Start();
                if (_sessionFolder is null)
                    throw new InvalidOperationException("Не подготовлена временная папка печати.");

                // Keep the staged files owned until preview is dismissed and
                // the Windows spooler has consumed its synchronous paginator.
                using var spool = PdfPrintSpool.Create(_sessionFolder);
                await StagePrintPagesAsync(selection, spool, token);
                token.ThrowIfCancellationRequested();

                var paginator = new PdfPagePrintPaginator(spool.Pages, selection.PreparedPages,
                    new Size(printer.PrintableAreaWidth, printer.PrintableAreaHeight));
                StatusText.Text = "Предпросмотр страниц печати…";
                var preview = new PdfPrintPreviewWindow(paginator) { Owner = HostWindow };
                if (preview.ShowDialog() != true) return false;
                token.ThrowIfCancellationRequested();
                StatusText.Text = "Передача документа службе печати Windows…";
                printer.PrintDocument(paginator, "ГЭТ PDF");
                return true;
            }, cancellation.Token);
            if (submitted)
            {
                _jobProgress.Complete();
                StatusText.Text = "Задание передано службе печати Windows.";
            }
            else
            {
                _jobProgress.Cancelled();
                StatusText.Text = "Печать отменена в окне предварительного просмотра.";
            }
        }
        catch (OperationCanceledException)
        {
            _jobProgress.Cancelled();
            StatusText.Text = "Подготовка печати отменена до отправки задания.";
        }
        catch (Exception ex)
        {
            _jobProgress.Fail();
            DiagnosticLog.Failure("pdf-print", ex);
            MessageBox.Show(HostWindow, ex.Message, "Печать PDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _batchCancel = null;
            _loading = false;
            SetControls();
        }
    }

    private static async Task<string> PostExportStatusAsync(string outputFile, string message)
    {
        try
        {
            // Availability itself verifies the approved SHA-256 manifest and
            // can reject a present-but-tampered optional qpdf executable.
            if (!PdfQpdfValidator.IsAvailable)
                return message + " (qpdf не установлен)";
            await PdfJobCoordinator.Shared.RunAsync(token =>
                PdfQpdfValidator.ValidateIfAvailableAsync(outputFile, token));
            return message + " · структура проверена qpdf";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("pdf-qpdf-validation", ex);
            return message +
                " · PDF сохранён, но qpdf не одобрен или проверка структуры не пройдена. " +
                "Проверьте файл перед использованием.";
        }
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null || _sourcePath is null || _loading || _rendering) return;
        if (_nativeDrafts.Count > 0)
        {
            MessageBox.Show(HostWindow,
                "Эскизы интерактивных аннотаций пока нельзя сохранить в PDF. " +
                "Сначала очистите их в меню «Аннотации · эскиз». " +
                "Исходный документ не изменён.",
                "Аннотации — только эскиз",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var sourcePath = _sourcePath;
        var selectedPages = _plan.Pages.ToArray();
        bool hasEdits = _plan.HasChanges;
        if (hasEdits && !_canModify) return;

        var dialog = new SaveFileDialog
        {
            Title = hasEdits ? "Сохранить изменённый PDF в новый файл"
                             : "Сохранить точную копию PDF (без изменения подписей)",
            Filter = "PDF (*.pdf)|*.pdf", AddExtension = true, DefaultExt = ".pdf",
            FileName = Path.GetFileNameWithoutExtension(_originalPath) +
                       (hasEdits ? "_изменённый.pdf" : "_копия.pdf"),
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(HostWindow) != true || !ValidateOutput(dialog.FileName)) return;
        if (hasEdits && !ConfirmStructuralExport()) return;

        string outputPath = dialog.FileName;
        _loading = true;
        SetControls();
        StatusText.Text = hasEdits
            ? "Создаём новую PDF-версию без растрирования…"
            : "Создаём точную копию PDF с проверкой SHA-256…";
        try
        {
            if (hasEdits)
            {
                await RunPdfFileJobAsync(token => Task.Run(() => PdfDocumentWriter.SaveAs(sourcePath, outputPath, selectedPages, token), token));
                _savedPlanFingerprint = CurrentPlanFingerprint();
                PersistPdfDraft();
                StatusText.Text = await PostExportStatusAsync(outputPath,
                    "Новая PDF-версия сохранена отдельно, оригинал не изменён.");
            }
            else
            {
                // No PDF parser or structural writer is involved: signed, encrypted
                // and interactive documents are copied as exact bytes.
                await RunPdfFileJobAsync(token => Task.Run(() => SaveUnchangedCopy(outputPath, token), token));
                StatusText.Text = "Точная PDF-копия сохранена. SHA-256 совпадает с оригиналом.";
            }
        }
        catch (OperationCanceledException) { StatusText.Text = "Операция PDF отменена; результат не опубликован."; }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("pdf-viewer-save", ex);
            StatusText.Text = "Сохранить PDF не удалось.";
            MessageBox.Show(HostWindow, ex.Message, "Сохранение PDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _loading = false; SetControls(); }
    }

    private void SaveUnchangedCopy(string outputPath, CancellationToken token) =>
        PdfByteCopy.Copy(_originalSourceCopyPath ?? throw new InvalidOperationException("Исходная копия PDF недоступна."), outputPath, token);

    private bool _resourcesReleased;

    // Phase 1: validate every tab before releasing any workspace.
    internal bool CanCloseDocument()
    {
        if (_resourcesReleased) return true;
        if (IsBusy)
        {
            MessageBox.Show(HostWindow, "Завершите обработку PDF перед закрытием вкладки.",
                "ГЭТ PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return DiscardEditsIfNeeded();
    }

    // Phase 2: release exactly this document's renderer, scratch and caches.
    internal async Task ReleaseForCloseAsync()
    {
        if (_resourcesReleased) return;
        _closing = true;
        await StopThumbnailWorkerAsync();
        await DocumentView.StopAsync();
        await Task.WhenAll(_textReadTasks.ToArray());
        try
        {
            PdfWorkspacePreferencesStore.Save(new PdfWorkspacePreferences(
                1, PdfWorkspaceToolGroups.FromIndex(Math.Min(3, ActiveToolIndex)),
                (int)DocumentView.LayoutMode));
        }
        catch (Exception ex) { DiagnosticLog.Failure("pdf-view-preferences", ex); }
        ReleaseDocument();
        _resourcesReleased = true;
    }
}
