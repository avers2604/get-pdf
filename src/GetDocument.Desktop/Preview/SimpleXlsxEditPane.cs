using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GetDocument.Core.Workspace;
using GetDocument.Office;

namespace GetDocument.Desktop.Preview;

/// <summary>In-tab editor of deliberately simple 10×10 text XLSX, not formulas or formatted workbooks.</summary>
internal sealed class SimpleXlsxEditPane : DockPanel, IDisposable
{
    private readonly WorkspaceDocumentTab _tab;
    private readonly Func<WorkspaceDocumentTab, string, string[][], Task> _saveRevision;
    private readonly TextBox[,] _cells = new TextBox[SimpleXlsxEditor.RowCount, SimpleXlsxEditor.ColumnCount];
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _save = new() { Content = "Сохранить новую редакцию…", IsEnabled = false };
    private readonly Button _discard = new() { Content = "Отменить правки", IsEnabled = false };
    private string[][] _initial = [];
    private string? _sha;
    private bool _initialized;
    private bool _disposed;

    public bool HasUnsavedChanges => !_disposed && _sha is not null &&
        Enumerable.Range(0, SimpleXlsxEditor.RowCount).Any(y =>
            Enumerable.Range(0, SimpleXlsxEditor.ColumnCount)
                .Any(x => (_cells[y, x].Text ?? "") != _initial[y][x]));

    public SimpleXlsxEditPane(WorkspaceDocumentTab tab,
        Func<WorkspaceDocumentTab, string, string[][], Task> saveRevision)
    {
        _tab = tab;
        _saveRevision = saveRevision;
        _status.Text = "Проверка XLSX…";
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12,
            Children = { _save, _discard } };
        var header = new StackPanel { Margin = new Thickness(18, 12), Spacing = 8,
            Children =
            {
                new TextBlock { Text = tab.Title, FontSize = 23, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Простой текстовый лист A1:J10. Формулы и сложное оформление не изменяются.",
                    TextWrapping = TextWrapping.Wrap },
                bar, _status
            }
        };
        DockPanel.SetDock(header, Dock.Top);
        Children.Add(header);

        var table = new StackPanel { Margin = new Thickness(18), Spacing = 5 };
        var letters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        letters.Children.Add(new TextBlock { Text = "#", Width = 38 });
        for (int x = 0; x < SimpleXlsxEditor.ColumnCount; x++)
            letters.Children.Add(new TextBlock { Text = ((char)('A' + x)).ToString(),
                Width = 120, HorizontalAlignment = HorizontalAlignment.Center });
        table.Children.Add(letters);

        for (int y = 0; y < SimpleXlsxEditor.RowCount; y++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            row.Children.Add(new TextBlock { Text = (y + 1).ToString(), Width = 38,
                VerticalAlignment = VerticalAlignment.Center });
            for (int x = 0; x < SimpleXlsxEditor.ColumnCount; x++)
            {
                var cell = new TextBox { Width = 120, Height = 36,
                    IsReadOnly = true, MaxLength = 2048 };
                cell.TextChanged += (_, _) => RefreshDirty();
                _cells[y, x] = cell;
                row.Children.Add(cell);
            }
            table.Children.Add(row);
        }
        Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = table
        });
        _save.Click += async (_, _) =>
        {
            if (_disposed || _sha is null || !HasUnsavedChanges) return;
            _save.IsEnabled = false;
            try { await _saveRevision(_tab, _sha, CurrentValues()); }
            finally { if (!_disposed) RefreshDirty(); }
        };
        _discard.Click += (_, _) =>
        {
            for (int y = 0; y < SimpleXlsxEditor.RowCount; y++)
                for (int x = 0; x < SimpleXlsxEditor.ColumnCount; x++)
                    _cells[y, x].Text = _initial[y][x];
            RefreshDirty();
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
            var snapshot = await Task.Run(() => SimpleXlsxEditor.Load(_tab.FilePath!));
            if (_disposed) return;
            _initial = snapshot.Cells;
            _sha = snapshot.Sha256;
            for (int y = 0; y < SimpleXlsxEditor.RowCount; y++)
                for (int x = 0; x < SimpleXlsxEditor.ColumnCount; x++)
                {
                    _cells[y, x].Text = _initial[y][x];
                    _cells[y, x].IsReadOnly = false;
                }
            _status.Text = "Доступен только текст и ограниченная сетка; исходник неизменяем.";
            RefreshDirty();
        }
        catch (Exception ex)
        {
            if (!_disposed)
                _status.Text = "Открыто только чтение: " + ex.Message;
        }
    }

    private string[][] CurrentValues() => Enumerable.Range(0, SimpleXlsxEditor.RowCount)
        .Select(y => Enumerable.Range(0, SimpleXlsxEditor.ColumnCount)
            .Select(x => _cells[y, x].Text ?? "").ToArray()).ToArray();

    private void RefreshDirty()
    {
        if (_disposed) return;
        _save.IsEnabled = _discard.IsEnabled = HasUnsavedChanges;
    }

    public void Dispose()
    {
        _disposed = true;
        for (int y = 0; y < SimpleXlsxEditor.RowCount; y++)
            for (int x = 0; x < SimpleXlsxEditor.ColumnCount; x++)
                _cells[y, x].IsReadOnly = true;
    }
}
