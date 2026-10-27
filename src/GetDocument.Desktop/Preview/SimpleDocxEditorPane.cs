using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using GetDocument.Office;

namespace GetDocument.Desktop.Preview;

/// <summary>Only for simple, unformatted DOCX. Saves a new document, never overwrites the source.</summary>
internal sealed class SimpleDocxEditorPane : DockPanel, IDisposable
{
    private readonly string _source;
    private readonly Func<string, Task> _onCopySaved;
    private readonly TextBox _editor = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        IsReadOnly = true,
        MinHeight = 400,
        Margin = new Thickness(20)
    };
    private readonly TextBlock _status = new() { Text = "Проверка DOCX…", TextWrapping = TextWrapping.Wrap };
    private readonly Button _save = new() { Content = "Сохранить новую редакцию…", IsEnabled = false };
    private readonly Button _discard = new()
    {
        Content = "Отменить правки", IsEnabled = false
    };
    private DocumentTextSnapshot? _snapshot;
    private string _baseline = "";
    private bool _loaded;
    private bool _disposed;

    public bool HasUnsavedChanges => _snapshot is not null && (_editor.Text ?? "") != _baseline;

    public SimpleDocxEditorPane(string path, Func<string, Task> onCopySaved)
    {
        _source = path;
        _onCopySaved = onCopySaved;
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(16),
            Children = { _save, _discard, _status }
        };
        DockPanel.SetDock(tools, Dock.Top);
        Children.Add(tools);
        Children.Add(new ScrollViewer { Content = _editor });
        _save.Click += async (_, _) => await SaveAsync();
        _discard.Click += (_, _) => _editor.Text = _baseline;
        _editor.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(TextBox.Text))
                _discard.IsEnabled = HasUnsavedChanges;
        };
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
            var snapshot = await Task.Run(() => SimpleDocxTextService.Open(_source));
            if (_disposed) return;
            _snapshot = snapshot;
            _baseline = snapshot.Text;
            _editor.Text = snapshot.Text;
            _editor.IsReadOnly = false;
            _save.IsEnabled = true;
            _status.Text = "Простой DOCX: только абзацы и текст. Исходный файл защищён.";
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            _editor.IsReadOnly = true;
            _save.IsEnabled = false;
            _status.Text = "Редактирование недоступно: " + ex.Message;
        }
    }

    private async Task SaveAsync()
    {
        if (_disposed || _snapshot is null) return;
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null)
        {
            _status.Text = "Диалог сохранения недоступен.";
            return;
        }
        IStorageFile? selected;
        try
        {
            selected = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Сохранить новую редакцию DOCX",
                SuggestedFileName = Path.GetFileNameWithoutExtension(_source) + "-редакция.docx",
                DefaultExtension = "docx",
                ShowOverwritePrompt = false,
                FileTypeChoices = [new FilePickerFileType("DOCX") { Patterns = ["*.docx"] }]
            });
        }
        catch (Exception ex)
        {
            _status.Text = "Ошибка выбора файла: " + ex.Message;
            return;
        }
        if (selected is null) return;
        if (!selected.Path.IsFile)
        {
            _status.Text = "Выберите обычную локальную или доступную сетевую папку.";
            return;
        }
        _save.IsEnabled = false;
        try
        {
            string path = selected.Path.LocalPath;
            string text = _editor.Text ?? "";
            await Task.Run(() => SimpleDocxTextService.SaveCopy(_snapshot, text, path));
            if (_disposed) return;
            _baseline = text;
            _discard.IsEnabled = false;
            _status.Text = "Новая редакция сохранена. Исходный файл не изменён.";
            await _onCopySaved(path);
        }
        catch (Exception ex)
        {
            if (!_disposed) _status.Text = "Не удалось сохранить новую редакцию: " + ex.Message;
        }
        finally { if (!_disposed) _save.IsEnabled = true; }
    }

    public void Dispose() => _disposed = true;
}
