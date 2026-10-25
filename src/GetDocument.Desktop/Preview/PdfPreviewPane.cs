using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GetDocument.Core.Preview;
using GetDocument.Desktop.Printing;
using PDFtoImage;

namespace GetDocument.Desktop.Preview;

/// <summary>
/// One page at a time; in-process PDFium is serialized across tabs.
/// Only displays rendered pixels. Forms/text selection/editing/printing are later adapters.
/// </summary>
internal sealed class PdfPreviewPane : DockPanel, IDisposable
{
    private readonly string _path;
    private readonly Image _pageImage = new()
    {
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center
    };
    private readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _first = new() { Content = "⇤", IsEnabled = false };
    private readonly Button _previous = new() { Content = "←", IsEnabled = false };
    private readonly Button _next = new() { Content = "→", IsEnabled = false };
    private readonly Button _last = new() { Content = "⇥", IsEnabled = false };
    private readonly TextBox _pageInput = new() { Width = 64, Watermark = "Стр." };
    private readonly Button _jump = new() { Content = "Перейти", IsEnabled = false };
    private readonly Button _zoomOut = new() { Content = "−" };
    private readonly Button _zoomIn = new() { Content = "+" };
    private readonly TextBlock _zoomLabel = new() { Text = "100%", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _searchInput = new() { Width = 220, Watermark = "Найти текст в PDF" };
    private readonly Button _search = new() { Content = "Найти" };
    private readonly Button _previousHit = new() { Content = "↑", IsEnabled = false };
    private readonly Button _nextHit = new() { Content = "↓", IsEnabled = false };
    private readonly TextBlock _searchStatus = new() { VerticalAlignment = VerticalAlignment.Center,
        MaxWidth = 450, TextWrapping = TextWrapping.Wrap };
    private CancellationTokenSource? _activeSearch;
    private IReadOnlyList<PdfTextPageMatch> _matches = [];
    private int _activeMatchIndex = -1;
    private readonly CancellationTokenSource _lifetime = new();
    private Bitmap? _currentBitmap;
    private bool _initialized;
    private bool _disposed;
    private int _currentPage;
    private int _pageCount;
    private int _zoomStep = PdfViewScale.DefaultStep;
    private int _rasterWidth;
    private int _rasterHeight;
    private long _generation;

    public PdfPreviewPane(string path)
    {
        _path = path;
        _message.Text = "Подготовка просмотра PDF…";
        var pageButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { _first, _previous, _pageLabel, _next, _last, _pageInput, _jump,
                _zoomOut, _zoomLabel, _zoomIn, _message }
        };
        var searchButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { _searchInput, _search, _previousHit, _nextHit, _searchStatus }
        };
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 8,
            Margin = new Thickness(16),
            Children = { pageButtons, searchButtons }
        };
        DockPanel.SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);
        Children.Add(new ScrollViewer
        {
            Content = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#EFF2F6")),
                Padding = new Thickness(24),
                Child = _pageImage
            }
        });

        _first.Click += (_, _) => _ = ShowPageAsync(0);
        _previous.Click += (_, _) => _ = ShowPageAsync(PdfPageNavigation.Move(_currentPage, -1, _pageCount));
        _next.Click += (_, _) => _ = ShowPageAsync(PdfPageNavigation.Move(_currentPage, 1, _pageCount));
        _last.Click += (_, _) => _ = ShowPageAsync(_pageCount - 1);
        _jump.Click += (_, _) => JumpToPage();
        ToolTip.SetTip(_zoomOut, "Уменьшить масштаб");
        ToolTip.SetTip(_zoomIn, "Увеличить масштаб");
        _zoomOut.Click += (_, _) => ChangeZoom(-1);
        _zoomIn.Click += (_, _) => ChangeZoom(1);
        _search.Click += async (_, _) => await SearchTextAsync();
        _searchInput.KeyDown += async (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            await SearchTextAsync();
        };
        _previousHit.Click += (_, _) => FindNext(reverse: true);
        _nextHit.Click += (_, _) => FindNext(reverse: false);
        _pageInput.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            JumpToPage();
        };
        AttachedToVisualTree += (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            _ = ShowPageAsync(0);
        };
    }

    public void FocusPageNumber()
    {
        if (_disposed || _pageCount < 1) return;
        _pageInput.Focus();
        _pageInput.SelectAll();
    }

    public void ChangeZoom(int delta)
    {
        if (_disposed) return;
        _zoomStep = PdfViewScale.Move(_zoomStep, delta);
        ApplyDisplayScale();
    }

    public void ResetZoom()
    {
        if (_disposed) return;
        _zoomStep = PdfViewScale.DefaultStep;
        ApplyDisplayScale();
    }

    private void ApplyDisplayScale()
    {
        _zoomLabel.Text = PdfViewScale.Percent(_zoomStep) + "%";
        _zoomOut.IsEnabled = _zoomStep > 0;
        _zoomIn.IsEnabled = _zoomStep + 1 < PdfViewScale.StepCount;
        if (_rasterWidth < 1 || _rasterHeight < 1) return;
        var size = PdfViewScale.GetDisplaySize(_rasterWidth, _rasterHeight, _zoomStep);
        _pageImage.Width = size.Width;
        _pageImage.Height = size.Height;
    }

    public void FocusSearch()
    {
        if (_disposed) return;
        _searchInput.Focus();
        _searchInput.SelectAll();
    }

    public void FindNext(bool reverse)
    {
        if (_disposed) return;
        if (_matches.Count == 0)
        {
            FocusSearch();
            return;
        }

        _activeMatchIndex = (_activeMatchIndex + (reverse ? _matches.Count - 1 : 1))
            % _matches.Count;
        var hit = _matches[_activeMatchIndex];
        _searchStatus.Text = $"Найдено на стр. {hit.PageIndex + 1}; " +
            $"страница {_activeMatchIndex + 1} из {_matches.Count}. " +
            "Подсветка текста пока не поддерживается.";
        _ = ShowPageAsync(hit.PageIndex);
    }

    private async Task SearchTextAsync()
    {
        if (_disposed) return;
        string query;
        try { query = PdfTextSearchPlan.NormalizeQuery(_searchInput.Text); }
        catch (ArgumentException)
        {
            _activeSearch?.Cancel();
            _activeSearch = null;
            _matches = [];
            _activeMatchIndex = -1;
            _previousHit.IsEnabled = _nextHit.IsEnabled = false;
            _search.IsEnabled = true;
            _searchStatus.Text = "Введите от 1 до 128 символов без переноса строки.";
            return;
        }

        _activeSearch?.Cancel();
        var current = new CancellationTokenSource();
        _activeSearch = current;
        _matches = [];
        _activeMatchIndex = -1;
        _previousHit.IsEnabled = _nextHit.IsEnabled = false;
        _searchStatus.Text = "Поиск по текстовому слою…";
        _search.IsEnabled = false;
        try
        {
            var result = await Task.Run(
                () => PdfTextSearchService.Search(_path, query, current.Token), current.Token);
            if (_disposed || current.IsCancellationRequested || !ReferenceEquals(_activeSearch, current))
                return;

            _matches = result.Pages;
            _previousHit.IsEnabled = _nextHit.IsEnabled = _matches.Count > 0;
            if (_matches.Count == 0)
            {
                _searchStatus.Text = result.PagesWithoutTextLayer == result.InspectedPages
                    ? "Текстового слоя нет: для сканов нужен OCR."
                    : "Совпадений не найдено в доступном текстовом слое.";
                if (result.PartialPages > 0)
                    _searchStatus.Text += " Часть длинных страниц проверена не полностью.";
                return;
            }

            _activeMatchIndex = 0;
            var first = _matches[0];
            _searchStatus.Text = $"Найдено страниц: {_matches.Count}. " +
                $"Первое совпадение на стр. {first.PageIndex + 1}. " +
                "Без подсветки текста.";
            if (result.PartialPages > 0)
                _searchStatus.Text += " Часть длинных страниц проверена не полностью.";
            _ = ShowPageAsync(first.PageIndex);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_disposed && ReferenceEquals(_activeSearch, current))
                _searchStatus.Text = "Не удалось прочитать текст PDF (возможно, файл защищён или повреждён).";
        }
        finally
        {
            if (ReferenceEquals(_activeSearch, current))
            {
                _activeSearch = null;
                if (!_disposed) _search.IsEnabled = true;
            }
            current.Dispose();
        }
    }

    private void JumpToPage()
    {
        if (_disposed || _pageCount < 1) return;
        if (!PdfPageNavigation.TryParsePage(_pageInput.Text, _pageCount, out var target))
        {
            _message.Text = $"Номер страницы должен быть от 1 до {_pageCount}.";
            return;
        }
        _ = ShowPageAsync(target);
    }

    private async Task ShowPageAsync(int index)
    {
        if (_disposed || index < 0 || _pageCount > 0 && index >= _pageCount) return;
        long generation = Interlocked.Increment(ref _generation);
        _message.Text = "Загрузка страницы…";
        _first.IsEnabled = _previous.IsEnabled = _next.IsEnabled = _last.IsEnabled = _jump.IsEnabled = false;
        try
        {
            var result = await Task.Run(
                () => Rasterize(_path, index, _lifetime.Token), _lifetime.Token);
            if (_disposed || generation != Interlocked.Read(ref _generation)) return;

            using var encoded = new MemoryStream(result.Png, writable: false);
            var bitmap = new Bitmap(encoded);
            var previous = _currentBitmap;
            _currentBitmap = bitmap;
            _pageImage.Source = bitmap;
            _rasterWidth = bitmap.PixelSize.Width;
            _rasterHeight = bitmap.PixelSize.Height;
            ApplyDisplayScale();
            previous?.Dispose();
            _currentPage = index;
            _pageCount = result.Count;
            _pageLabel.Text = $"Страница {index + 1} из {_pageCount}";
            _pageInput.Text = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _message.Text = "Только просмотр";
            _first.IsEnabled = _previous.IsEnabled = index > 0;
            _next.IsEnabled = _last.IsEnabled = index + 1 < _pageCount;
            _jump.IsEnabled = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_disposed || generation != Interlocked.Read(ref _generation)) return;
            _message.Text = "PDF не удалось отобразить: " + ex.Message;
            _first.IsEnabled = _previous.IsEnabled = _currentPage > 0;
            _next.IsEnabled = _last.IsEnabled = _pageCount > 0 && _currentPage + 1 < _pageCount;
            _jump.IsEnabled = _pageCount > 0;
        }
    }

    private static (byte[] Png, int Count) Rasterize(string path, int index, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var length = new FileInfo(path).Length;
        PdfPreviewPlan.ValidateInputLength(length);

        PdfNativeGate.Instance.Wait(token);
        try
        {
            using var source = File.OpenRead(path);
            int count = PDFtoImage.Conversion.GetPageCount(source, leaveOpen: true);
            PdfPreviewPlan.ValidatePage(index, count);
            source.Position = 0;
            var size = PDFtoImage.Conversion.GetPageSize(source, page: index, leaveOpen: true);
            var raster = PdfPreviewPlan.GetRasterSize(size.Width, size.Height);
            source.Position = 0;
            token.ThrowIfCancellationRequested();
            using var output = new MemoryStream();
            PDFtoImage.Conversion.SavePng(output, source, page: index, leaveOpen: true,
                options: new PDFtoImage.RenderOptions(Width: raster.Width, Height: raster.Height,
                    WithAnnotations: true, WithFormFill: true));
            token.ThrowIfCancellationRequested();
            return (output.ToArray(), count);
        }
        finally { PdfNativeGate.Instance.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _activeSearch?.Cancel();
        _pageImage.Source = null;
        _currentBitmap?.Dispose();
        // Do not dispose CTS while a running render still observes its token.
        // The lifetime CTS is reclaimed after completion/GC in this prototype.
    }
}
