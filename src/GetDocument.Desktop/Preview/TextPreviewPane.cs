using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GetDocument.Desktop.Preview;

internal sealed class TextPreviewPane : ScrollViewer
{
    private readonly string _path;
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(24) };
    private bool _initialized;

    public TextPreviewPane(string path)
    {
        _path = path;
        Content = _text;
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
            var content = await Task.Run(() =>
            {
                if (new FileInfo(_path).Length > 2L * 1024 * 1024)
                    throw new InvalidDataException("Текстовый файл превышает лимит предпросмотра 2 МиБ.");
                return File.ReadAllText(_path);
            });
            _text.Text = content;
        }
        catch (Exception ex)
        {
            _text.Text = "Не удалось открыть текстовый файл: " + ex.Message;
        }
    }
}
