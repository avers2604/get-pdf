using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace GetDocument.Desktop.Preview;

internal sealed class ImagePreviewPane : DockPanel, IDisposable
{
    private readonly string _path;
    private readonly Image _image = new() { Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _status = new();
    private Bitmap? _bitmap;
    private bool _disposed;
    private bool _initialized;

    public ImagePreviewPane(string path)
    {
        _path = path;
        var header = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        header.Children.Add(new TextBlock { Text = "Просмотр изображения · без изменения оригинала" });
        header.Children.Add(_status);
        DockPanel.SetDock(header, Dock.Top);
        Children.Add(header);
        Children.Add(new ScrollViewer
        {
            Content = new Border
            {
                Padding = new Thickness(24),
                Background = new SolidColorBrush(Color.Parse("#EFF2F6")),
                Child = _image
            }
        });
        AttachedToVisualTree += (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            _ = LoadAsync();
        };
    }

    private async Task LoadAsync()
    {
        try
        {
            // Image decoder and decoded pixels must be placed in an isolated worker before release.
            // The first-pass viewer only permits a bounded compressed file size and 1400px preview.
            var bitmap = await Task.Run(() =>
            {
                if (new FileInfo(_path).Length > 32L * 1024 * 1024)
                    throw new InvalidDataException("Изображение превышает лимит предпросмотра 32 МиБ.");
                using var stream = File.OpenRead(_path);
                return Bitmap.DecodeToWidth(stream, 1400);
            });
            if (_disposed) { bitmap.Dispose(); return; }
            _bitmap = bitmap;
            _image.Source = bitmap;
            _status.Text = "Изображение открыто";
        }
        catch (Exception ex)
        {
            if (!_disposed) _status.Text = "Не удалось открыть изображение: " + ex.Message;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _image.Source = null;
        _bitmap?.Dispose();
    }
}
