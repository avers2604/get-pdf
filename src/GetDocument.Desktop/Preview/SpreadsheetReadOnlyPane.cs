using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GetDocument.Office.Spreadsheets;

namespace GetDocument.Desktop.Preview;

/// <summary>
/// Read-only first-sheet preview for legacy XLS/XLSB; displayed inside the same
/// Avalonia document tab and intentionally has NO Save button.
/// </summary>
internal sealed class SpreadsheetReadOnlyPane : DockPanel, IDisposable
{
    private readonly string _path;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBlock _status = new()
    {
        Text = "Чтение таблицы…",
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBlock _heading = new() { Classes = { "title" } };
    private readonly ListBox _rows = new();
    private bool _loaded;
    private bool _disposed;

    public SpreadsheetReadOnlyPane(string path)
    {
        _path = path;
        var top = new StackPanel { Margin = new Thickness(18), Spacing = 8 };
        _heading.Text = Path.GetFileName(path);
        top.Children.Add(_heading);
        top.Children.Add(new TextBlock
        {
            Text = "Только просмотр. Сложные Excel-файлы не изменяются и не конвертируются.",
            TextWrapping = TextWrapping.Wrap
        });
        top.Children.Add(_status);
        DockPanel.SetDock(top, Dock.Top);
        Children.Add(top);
        Children.Add(_rows);
        AttachedToVisualTree += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            _ = LoadAsync();
        };
    }

    private async Task LoadAsync()
    {
        try
        {
            var preview = await Task.Run(
                () => ReadOnlySpreadsheetPreview.Read(
                    _path, cancellationToken: _lifetime.Token), _lifetime.Token);
            if (_disposed) return;
            _heading.Text = $"{Path.GetFileName(_path)} — {preview.SheetName}";
            var display = new List<string>
            {
                "Отображаются первые 100 строк и 20 столбцов; редактирование отключено."
            };
            for (int row = 0; row < preview.Rows.Count; row++)
            {
                display.Add($"{row + 1,3}: " +
                    string.Join("  │  ", preview.Rows[row]));
            }
            _rows.ItemsSource = display;
            _status.Text = preview.IsTruncated
                ? "Показана только часть первой страницы/листа. Для полного просмотра требуется офисный движок."
                : $"Прочитано строк: {preview.Rows.Count}.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed) _status.Text = "Таблица недоступна для просмотра: " + ex.Message;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _rows.ItemsSource = null;
    }
}
