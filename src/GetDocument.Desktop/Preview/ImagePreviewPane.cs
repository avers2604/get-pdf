using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using GetDocument.Images;

namespace GetDocument.Desktop.Preview;

/// <summary>
/// Light-weight preview + NetVips-backed non-destructive image tools.
/// No in-process use of the GUP-Scan driver stack. Cropping coordinates
/// refer to actual source pixels *after* rotation.
/// </summary>
internal sealed class ImagePreviewPane : DockPanel, IDisposable
{
    private readonly string _path;
    private readonly Func<string, Task> _openSavedCopy;
    private readonly Image _image = new()
    {
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _save = new()
    {
        Content = "Сохранить новую редакцию PNG…",
        IsEnabled = false
    };
    private readonly Button _discard = new() { Content = "Сбросить", IsEnabled = false };
    private readonly TextBox _x = new() { Width = 66, PlaceholderText = "X" };
    private readonly TextBox _y = new() { Width = 66, PlaceholderText = "Y" };
    private readonly TextBox _w = new() { Width = 66, PlaceholderText = "Шир." };
    private readonly TextBox _h = new() { Width = 66, PlaceholderText = "Выс." };
    private Bitmap? _bitmap;
    private string? _fingerprint;
    private int _turns;
    private bool _flip;
    private bool _disposed;
    private bool _initialized;
    private bool _saving;

    public bool HasUnsavedChanges => !_disposed && (_turns != 0 || _flip ||
        new[] { _x, _y, _w, _h }.Any(t => !string.IsNullOrWhiteSpace(t.Text)));

    public ImagePreviewPane(string path, Func<string, Task> openSavedCopy)
    {
        _path = path;
        _openSavedCopy = openSavedCopy;
        var header = new StackPanel { Margin = new Thickness(16), Spacing = 9 };
        header.Children.Add(new TextBlock { Text = "ГЭТ Изображения · редактирование без изменения оригинала" });

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var left = new Button { Content = "↶ 90°" };
        left.Click += (_, _) => { _turns = (_turns + 3) % 4; UpdateState(); };
        tools.Children.Add(left);
        var right = new Button { Content = "↷ 90°" };
        right.Click += (_, _) => { _turns = (_turns + 1) % 4; UpdateState(); };
        tools.Children.Add(right);
        var flip = new Button { Content = "↔ Отразить" };
        flip.Click += (_, _) => { _flip = !_flip; UpdateState(); };
        tools.Children.Add(flip);
        tools.Children.Add(new TextBlock
        {
            Text = "Обрезка (пиксели после поворота):",
            VerticalAlignment = VerticalAlignment.Center
        });
        foreach (TextBox input in new[] { _x, _y, _w, _h })
        {
            input.TextChanged += (_, _) => UpdateState();
            tools.Children.Add(input);
        }
        header.Children.Add(tools);
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        commands.Children.Add(_save);
        commands.Children.Add(_discard);
        header.Children.Add(commands);
        header.Children.Add(new TextBlock
        {
            Text = "Изменения применятся при сохранении; предварительный просмотр показывает исходник. " +
                "Файл редактируется только при поддерживаемом формате PNG/JPG/WebP.",
            TextWrapping = TextWrapping.Wrap
        });
        header.Children.Add(_status);
        _save.Click += async (_, _) => await SaveNewRevisionAsync();
        _discard.Click += (_, _) => ResetEdits();
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

    private void UpdateState()
    {
        if (_disposed) return;
        bool canEdit = _fingerprint is not null;
        _save.IsEnabled = canEdit && HasUnsavedChanges && !_saving;
        _discard.IsEnabled = HasUnsavedChanges && !_saving;
    }

    private void ResetEdits()
    {
        _turns = 0;
        _flip = false;
        foreach (var control in new[] { _x, _y, _w, _h }) control.Text = "";
        UpdateState();
    }

    private ImageEditPlan ParsePlan()
    {
        var inputs = new[] { _x.Text, _y.Text, _w.Text, _h.Text };
        ImageCrop? crop = null;
        if (inputs.Any(v => !string.IsNullOrWhiteSpace(v)))
        {
            if (inputs.Any(v => !int.TryParse(v, out _)))
                throw new ArgumentException("Укажите все четыре целочисленные координаты обрезки.");
            crop = new ImageCrop(int.Parse(inputs[0]!), int.Parse(inputs[1]!),
                int.Parse(inputs[2]!), int.Parse(inputs[3]!));
        }
        return new ImageEditPlan(_turns, _flip, crop);
    }

    private async Task LoadAsync()
    {
        try
        {
            var data = await Task.Run(() =>
            {
                if (new FileInfo(_path).Length > ImageEditService.MaxInputBytes)
                    throw new InvalidDataException("Изображение превышает лимит 32 МиБ.");
                using var stream = File.OpenRead(_path);
                var bitmap = Bitmap.DecodeToWidth(stream, 1400);
                string? fingerprint = null;
                try
                {
                    ImageEditPlan.ValidateSourceExtension(_path);
                    fingerprint = ImageEditService.FingerprintSource(_path);
                }
                catch (NotSupportedException) { }
                return (bitmap, fingerprint);
            });
            if (_disposed) { data.bitmap.Dispose(); return; }
            _bitmap = data.bitmap;
            _image.Source = data.bitmap;
            _fingerprint = data.fingerprint;
            _status.Text = _fingerprint is null
                ? "Просмотр доступен. Для редактирования этого формата нужен отдельный модуль."
                : "Изображение открыто. Оригинал остаётся неизменным.";
            UpdateState();
        }
        catch (Exception ex)
        {
            if (!_disposed) _status.Text = "Не удалось открыть изображение: " + ex.Message;
        }
    }

    private async Task SaveNewRevisionAsync()
    {
        if (_disposed || _fingerprint is null || _saving) return;
        ImageEditPlan plan;
        try { plan = ParsePlan(); }
        catch (Exception ex) { _status.Text = ex.Message; return; }
        if (!plan.HasChanges) return;
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null) { _status.Text = "Диалог сохранения недоступен."; return; }

        IStorageFile? selected;
        try
        {
            selected = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Создать новую редакцию изображения",
                SuggestedFileName = Path.GetFileNameWithoutExtension(_path) + "-редакция.png",
                DefaultExtension = "png",
                ShowOverwritePrompt = false,
                FileTypeChoices = [new FilePickerFileType("PNG") { Patterns = ["*.png"] }]
            });
        }
        catch (Exception ex) { _status.Text = "Ошибка выбора пути: " + ex.Message; return; }
        if (selected is null) return;
        if (!selected.Path.IsFile) { _status.Text = "Требуется доступная файловая папка."; return; }

        _saving = true;
        UpdateState();
        try
        {
            string destination = selected.Path.LocalPath;
            string expected = _fingerprint;
            await Task.Run(() =>
                ImageEditService.SaveNewRevision(_path, destination, plan, expected));
            if (_disposed) return;
            ResetEdits();
            _status.Text = "Новая редакция PNG создана; оригинал не изменён.";
            await _openSavedCopy(destination);
        }
        catch (Exception ex)
        {
            if (!_disposed) _status.Text = "Редакция не сохранена: " + ex.Message;
        }
        finally
        {
            _saving = false;
            if (!_disposed) UpdateState();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _image.Source = null;
        _bitmap?.Dispose();
    }
}
