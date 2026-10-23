using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GetDocument.Core.Workspace;
using GetDocument.Office;

namespace GetDocument.Desktop.Preview;

/// <summary>Deliberately limited in-tab plain paragraph editor; complex DOCX are blocked, not flattened.</summary>
internal sealed class SimpleDocxEditPane : DockPanel, IDisposable
{
    private readonly WorkspaceDocumentTab _tab;
    private readonly Func<WorkspaceDocumentTab, string, string, Task> _saveRevision;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _text = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        IsReadOnly = true
    };
    private readonly Button _save = new() { Content = "Сохранить новую редакцию…", IsEnabled = false };
    private readonly Button _discard = new() { Content = "Отменить правки", IsEnabled = false };
    private string? _sha256;
    private string _originalText = "";
    private bool _initialized;
    private bool _disposed;

    public bool HasUnsavedChanges =>
        !_disposed && _sha256 is not null &&
        !string.Equals(_text.Text ?? "", _originalText, StringComparison.Ordinal);

    public SimpleDocxEditPane(WorkspaceDocumentTab tab,
        Func<WorkspaceDocumentTab, string, string, Task> saveRevision)
    {
        _tab = tab;
        _saveRevision = saveRevision;
        _status.Text = "Проверка совместимости DOCX…";
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(16, 12),
            Children = { _save, _discard }
        };
        _save.Click += async (_, _) =>
        {
            if (_sha256 is null || _disposed) return;
            _save.IsEnabled = false;
            try { await _saveRevision(_tab, _sha256, _text.Text ?? ""); }
            finally { if (!_disposed) _save.IsEnabled = true; }
        };
        _discard.Click += (_, _) => _text.Text = _originalText;
        var header = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = tab.Title, FontSize = 23, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = "Упрощённое редактирование абзацев. Новая редакция сохраняется отдельно; " +
                           "сложные DOCX защищены от изменения.",
                    TextWrapping = TextWrapping.Wrap
                },
                controls,
                _status
            }
        };
        var headerContainer = new Border { Child = header, Padding = new Thickness(18) };
        DockPanel.SetDock(headerContainer, Dock.Top);
        Children.Add(headerContainer);
        Children.Add(new Border { Padding = new Thickness(24), Child = _text });
        _text.TextChanged += (_, _) =>
        {
            _discard.IsEnabled = HasUnsavedChanges;
        };
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
            var snapshot = await Task.Run(() => SimpleDocxEditor.Load(_tab.FilePath!));
            if (_disposed) return;
            _originalText = snapshot.Text;
            _sha256 = snapshot.Sha256;
            _text.Text = snapshot.Text;
            _text.IsReadOnly = false;
            _save.IsEnabled = true;
            _status.Text = "Режим простого DOCX · исходный документ только для чтения";
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _text.IsReadOnly = true;
                _save.IsEnabled = false;
                _status.Text = "Редактирование недоступно: " + ex.Message;
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _text.IsReadOnly = true;
    }
}
