using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GetDocument.Core.Preview;
using PDFtoImage;

namespace GetDocument.Desktop.Preview;

/// <summary>
/// One page at a time; in-process PDFium is serialized across tabs.
/// Only displays rendered pixels. Forms/text selection/editing/printing are later adapters.
/// </summary>
internal sealed class PdfPreviewPane : DockPanel, IDisposable
{
    private static readonly SemaphoreSlim NativeRenderGate = new(1, 1);
    private readonly string _path;
    private readonly Image _pageImage = new()
    {
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center
    };
    private readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _previous = new() { Content = "←", IsEnabled = false };
    private readonly Button _next = new() { Content = "→", IsEnabled = false };
    private readonly CancellationTokenSource _lifetime = new();
    private Bitmap? _currentBitmap;
    private bool _initialized;
    private bool _disposed;
    private int _currentPage;
    private int _pageCount;
    private long _generation;

    public PdfPreviewPane(string path)
    {
        _path = path;
        _message.Text = "Подготовка просмотра PDF…";
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(16),
            Children = { _previous, _pageLabel, _next, _message }
        };
        DockPanel.SetDock(buttons, Dock.Top);
        Children.Add(buttons);
        Children.Add(new ScrollViewer
        {
            Content = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#EFF2F6")),
                Padding = new Thickness(24),
                Child = _pageImage
            }
        });

        _previous.Click += (_, _) => _ = ShowPageAsync(_currentPage - 1);
        _next.Click += (_, _) => _ = ShowPageAsync(_currentPage + 1);
        AttachedToVisualTree += (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            _ = ShowPageAsync(0);
        };
    }

    private async Task ShowPageAsync(int index)
    {
        if (_disposed || index < 0 || _pageCount > 0 && index >= _pageCount) return;
        long generation = Interlocked.Increment(ref _generation);
        _message.Text = "Загрузка страницы…";
        _previous.IsEnabled = _next.IsEnabled = false;
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
            previous?.Dispose();
            _currentPage = index;
            _pageCount = result.Count;
            _pageLabel.Text = $"Страница {index + 1} из {_pageCount}";
            _message.Text = "Только просмотр";
            _previous.IsEnabled = index > 0;
            _next.IsEnabled = index + 1 < _pageCount;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_disposed || generation != Interlocked.Read(ref _generation)) return;
            _message.Text = "PDF не удалось отобразить: " + ex.Message;
            _previous.IsEnabled = _currentPage > 0;
            _next.IsEnabled = _pageCount > 0 && _currentPage + 1 < _pageCount;
        }
    }

    private static (byte[] Png, int Count) Rasterize(string path, int index, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var length = new FileInfo(path).Length;
        PdfPreviewPlan.ValidateInputLength(length);

        NativeRenderGate.Wait(token);
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
                options: new RenderOptions(Width: raster.Width, Height: raster.Height,
                    WithAnnotations: true, WithFormFill: true));
            token.ThrowIfCancellationRequested();
            return (output.ToArray(), count);
        }
        finally { NativeRenderGate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _pageImage.Source = null;
        _currentBitmap?.Dispose();
        // Do not dispose CTS while a running render still observes its token.
        // The lifetime CTS is reclaimed after completion/GC in this prototype.
    }
}
