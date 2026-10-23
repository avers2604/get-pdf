using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GetDocument.Core.Files;

namespace GetDocument.Desktop.Files;

/// <summary>
/// One in-app tab for ordinary Windows Server 2016 shares / Linux Samba mounts.
/// No persistent paths, no credentials, no elevated identity and no background index.
/// </summary>
internal sealed class GetFilesPane : DockPanel, IDisposable
{
    private readonly Action<string> _openFile;
    private readonly TextBox _root = new()
    {
        Width = 350,
        Watermark = @"\\сервер\общая_папка или /mnt/get-share"
    };
    private readonly TextBox _filter = new() { Watermark = "Фильтр текущей папки", Width = 240 };
    private readonly TextBlock _location = new() { Text = "Папка не выбрана" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _list = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private RootedFileBrowser? _browser;
    private SharedFolderSnapshot? _snapshot;
    private SharedFileEntry[] _shown = [];
    private FileSystemWatcher? _watcher;
    private bool _refreshing;
    private bool _disposed;
    private int _version;
    private int _dirty;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

    public GetFilesPane(Action<string> openFile)
    {
        _openFile = openFile;
        var header = new StackPanel
        {
            Spacing = 9,
            Margin = new Thickness(16, 12)
        };
        header.Children.Add(new TextBlock
        {
            Text = "ГЭТ Файлы · существующее серверное хранилище",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold
        });
        header.Children.Add(new TextBlock
        {
            Text = "Windows: путь UNC или сетевой диск. Linux: подключённая папка Samba. " +
                   "Доступ — по правам текущего сотрудника. Пароли не сохраняются.",
            TextWrapping = TextWrapping.Wrap
        });
        var connection = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        connection.Children.Add(_root);
        var connect = new Button { Content = "Подключить" };
        connect.Click += async (_, _) => await ConnectAsync(_root.Text);
        connection.Children.Add(connect);
        var pick = new Button { Content = "Выбрать папку…" };
        pick.Click += async (_, _) => await PickFolderAsync();
        connection.Children.Add(pick);
        header.Children.Add(connection);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var back = new Button { Content = "↑ Вверх" };
        back.Click += async (_, _) => await GoUpAsync();
        controls.Children.Add(back);
        var refresh = new Button { Content = "↻ Обновить" };
        refresh.Click += async (_, _) => await RefreshAsync();
        controls.Children.Add(refresh);
        var open = new Button { Content = "Открыть / перейти" };
        open.Click += async (_, _) => await ActivateSelectedAsync();
        controls.Children.Add(open);
        controls.Children.Add(_filter);
        header.Children.Add(controls);
        header.Children.Add(_location);
        header.Children.Add(_status);
        _status.Text = "Выберите доступную папку или укажите адрес.";
        DockPanel.SetDock(header, Dock.Top);
        Children.Add(header);
        Children.Add(_list);

        _filter.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(TextBox.Text)) DisplayItems();
        };
        _list.DoubleTapped += async (_, _) => await ActivateSelectedAsync();
        _timer.Tick += (_, _) =>
        {
            if (_disposed || _browser is null || !IsVisible || _refreshing) return;
            bool dirty = Interlocked.Exchange(ref _dirty, 0) != 0;
            if (dirty || DateTimeOffset.UtcNow - _lastRefresh >= TimeSpan.FromSeconds(30))
                _ = RefreshAsync();
        };
        AttachedToVisualTree += (_, _) => { if (!_disposed) _timer.Start(); };
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    private async Task PickFolderAsync()
    {
        try
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (provider is null || !provider.CanPickFolder)
            {
                _status.Text = "Системный выбор папки недоступен. Введите путь к сетевому каталогу.";
                return;
            }
            var picked = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Выберите доступную папку ГЭТ",
                AllowMultiple = false
            });
            if (picked.Count == 0) return;
            string? path = picked[0].TryGetLocalPath();
            if (path is null)
                _status.Text = "Системный каталог не предоставляет файловый путь.";
            else
            {
                _root.Text = path;
                await ConnectAsync(path);
            }
        }
        catch (Exception ex) { if (!_disposed) _status.Text = "Выбор папки: " + ex.Message; }
    }

    private async Task ConnectAsync(string? path)
    {
        if (_disposed || _refreshing) return;
        if (string.IsNullOrWhiteSpace(path))
        {
            _status.Text = "Введите сетевой путь или выберите папку.";
            return;
        }
        _refreshing = true;
        _status.Text = "Подключение к каталогу…";
        try
        {
            var browser = await Task.Run(() => new RootedFileBrowser(path));
            if (_disposed) return;
            _browser = browser;
            _version++;
            SetupWatcher(browser.CurrentPath);
            _location.Text = browser.CurrentPath;
            _snapshot = null;
            _list.ItemsSource = Array.Empty<string>();
            _status.Text = "Каталог подключён. Список обновляется…";
        }
        catch (Exception ex)
        {
            if (!_disposed) _status.Text = "Каталог недоступен: " + ex.Message;
        }
        finally { _refreshing = false; }
        await RefreshAsync();
    }

    private async Task GoUpAsync()
    {
        if (_disposed || _browser is null || _refreshing) return;
        _refreshing = true;
        try
        {
            bool changed = await Task.Run(() => _browser.GoUp());
            if (changed) { _version++; SetupWatcher(_browser.CurrentPath); }
        }
        catch (Exception ex) { _status.Text = "Не удалось перейти выше: " + ex.Message; }
        finally { _refreshing = false; }
        await RefreshAsync();
    }

    private async Task ActivateSelectedAsync()
    {
        if (_disposed || _browser is null || _refreshing) return;
        int selected = _list.SelectedIndex;
        if (selected < 0 || selected >= _shown.Length) return;
        SharedFileEntry entry = _shown[selected];
        _refreshing = true;
        try
        {
            if (entry.IsDirectory)
            {
                await Task.Run(() => _browser.NavigateTo(entry.FullPath));
                if (_disposed) return;
                _version++;
                SetupWatcher(_browser.CurrentPath);
            }
            else if (entry.CanOpen)
            {
                var full = await Task.Run(() => _browser.ResolveOpenableFile(entry.FullPath));
                if (!_disposed) _openFile(full);
                return;
            }
            else
            {
                _status.Text = "Для данного расширения ещё нет встроенного просмотрщика.";
                return;
            }
        }
        catch (Exception ex)
        {
            if (!_disposed) _status.Text = "Файл или папка недоступны: " + ex.Message;
        }
        finally { _refreshing = false; }
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_disposed || _browser is null || _refreshing) return;
        _refreshing = true;
        RootedFileBrowser browser = _browser;
        int generation = _version;
        try
        {
            var snapshot = await Task.Run(() => browser.Scan());
            if (_disposed || generation != _version || !ReferenceEquals(browser, _browser)) return;
            _snapshot = snapshot;
            _location.Text = snapshot.FolderPath;
            _lastRefresh = DateTimeOffset.UtcNow;
            DisplayItems();
            _status.Text = snapshot.IsTruncated
                ? "Показаны первые 400 элементов. Для больших папок нужен серверный поиск."
                : $"Найдено элементов: {snapshot.Entries.Count}.";
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _snapshot = null;
                _shown = [];
                _list.ItemsSource = Array.Empty<string>();
                _status.Text = "Каталог недоступен или изменились права: " + ex.Message;
            }
        }
        finally { _refreshing = false; }
    }

    private void DisplayItems()
    {
        if (_disposed || _snapshot is null) return;
        string filter = (_filter.Text ?? "").Trim();
        _shown = _snapshot.Entries
            .Where(e => filter.Length == 0 ||
                        e.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();
        _list.ItemsSource = _shown
            .Select(e => (e.IsDirectory ? "📁  " : "📄  ") + e.Name +
                (!e.IsDirectory && !e.CanOpen ? "  (просмотр недоступен)" : ""))
            .ToArray();
    }

    private void SetupWatcher(string folder)
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size
            };
            watcher.Created += OnChange;
            watcher.Deleted += OnChange;
            watcher.Changed += OnChange;
            watcher.Renamed += OnRename;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception)
        {
            // Some SMB/Samba mounts do not support reliable notifications.
            // Mandatory periodic polling below still reconciles the view.
            Interlocked.Exchange(ref _dirty, 1);
        }
    }

    private void OnChange(object sender, FileSystemEventArgs e) =>
        Interlocked.Exchange(ref _dirty, 1);
    private void OnRename(object sender, RenamedEventArgs e) =>
        Interlocked.Exchange(ref _dirty, 1);
    private void OnWatcherError(object sender, ErrorEventArgs e) =>
        Interlocked.Exchange(ref _dirty, 1);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _version++;
        _timer.Stop();
        _watcher?.Dispose();
    }
}
