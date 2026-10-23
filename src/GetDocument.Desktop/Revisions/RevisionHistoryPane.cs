using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using GetDocument.Office;
using GetDocument.Revisions;

namespace GetDocument.Desktop.Revisions;

/// <summary>
/// Explicit opt-in metadata folder. This pane never silently modifies the selected
/// document, writes into a WORM directory, or uses a remote/public server.
/// </summary>
internal sealed class RevisionHistoryPane : DockPanel, IDisposable
{
    private readonly string _originalPath;
    private readonly Action<string> _openRevision;
    private readonly TextBlock _status = new()
    {
        Text = "Выберите существующую папку для журнала редакций. " +
               "Записи будут храниться там с правами файловой системы.",
        TextWrapping = TextWrapping.Wrap
    };
    private readonly ListBox _revisions = new() { MinHeight = 130 };
    private readonly ListBox _comments = new() { MinHeight = 100 };
    private readonly TextBox _author = new()
    {
        Width = 235, Watermark = "Имя автора (не подтверждено)"
    };
    private readonly TextBox _comment = new()
    {
        Width = 390, Height = 75, Watermark = "Текст замечания",
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBox _comparison = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        MinHeight = 140, MaxHeight = 320
    };
    private readonly Button _register = new() { Content = "Добавить существующую редакцию", IsEnabled = false };
    private readonly Button _verify = new() { Content = "Проверить SHA-256", IsEnabled = false };
    private readonly Button _compare = new() { Content = "Сравнить текст", IsEnabled = false };
    private readonly Button _note = new() { Content = "Добавить комментарий", IsEnabled = false };

    private FileRevisionJournal? _journal;
    private IReadOnlyList<RevisionRecord> _current = [];
    private bool _disposed;
    private bool _busy;

    public RevisionHistoryPane(string originalPath, Action<string> openRevision)
    {
        _originalPath = originalPath;
        _openRevision = openRevision;

        var layout = new StackPanel { Margin = new Thickness(24), Spacing = 10 };
        layout.Children.Add(new TextBlock
        {
            Text = "История редакций и комментарии", FontSize = 25,
            FontWeight = FontWeight.SemiBold
        });
        layout.Children.Add(new TextBlock
        {
            Text = "Документ: " + originalPath, TextWrapping = TextWrapping.Wrap
        });
        layout.Children.Add(new TextBlock
        {
            Text = "Локальный прототип. Авторы комментариев пока не проверяются через AD. " +
                   "Сохранённые версии — ссылки на существующие файлы, а не резервные копии. " +
                   "Исходник и WORM не изменяются.",
            TextWrapping = TextWrapping.Wrap
        });
        var metadata = new Button { Content = "Выбрать папку журнала…" };
        metadata.Click += async (_, _) => await SelectMetadataFolderAsync();
        layout.Children.Add(metadata);
        layout.Children.Add(_status);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _register.Click += async (_, _) => await RegisterAsync();
        actions.Children.Add(_register);
        var refresh = new Button { Content = "Обновить" };
        refresh.Click += async (_, _) => await RefreshAsync();
        actions.Children.Add(refresh);
        layout.Children.Add(actions);
        layout.Children.Add(new TextBlock { Text = "Зарегистрированные редакции" });
        _revisions.SelectionChanged += (_, _) => UpdateButtons();
        layout.Children.Add(_revisions);

        var versions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _verify.Click += async (_, _) => await VerifyAsync();
        versions.Children.Add(_verify);
        var open = new Button { Content = "Открыть во вкладке" };
        open.Click += (_, _) =>
        {
            var selected = SelectedRevision;
            if (selected is not null) _openRevision(selected.RevisionPath);
        };
        versions.Children.Add(open);
        _compare.Click += async (_, _) => await CompareAsync();
        versions.Children.Add(_compare);
        layout.Children.Add(versions);
        layout.Children.Add(new TextBlock
        {
            Text = "Сравнение: только текст TXT/MD и простых DOCX, без анализа форматирования."
        });
        layout.Children.Add(_comparison);
        layout.Children.Add(new TextBlock { Text = "Комментарии" });
        layout.Children.Add(_comments);
        var commentFields = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        commentFields.Children.Add(_author);
        commentFields.Children.Add(_comment);
        _note.Click += async (_, _) => await AddCommentAsync();
        commentFields.Children.Add(_note);
        layout.Children.Add(commentFields);

        Children.Add(new ScrollViewer { Content = layout });
    }

    private RevisionRecord? SelectedRevision =>
        _revisions.SelectedIndex is int i && i >= 0 && i < _current.Count ? _current[i] : null;

    private void UpdateButtons()
    {
        bool connected = _journal is not null && !_busy && !_disposed;
        _register.IsEnabled = connected;
        _note.IsEnabled = connected;
        _verify.IsEnabled = connected && SelectedRevision is not null;
        _compare.IsEnabled = connected && SelectedRevision is not null;
    }

    private async Task SelectMetadataFolderAsync()
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null) { _status.Text = "Системный выбор папки недоступен."; return; }
        try
        {
            var selected = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Выбрать существующую папку метаданных версий",
                AllowMultiple = false
            });
            string? path = selected.FirstOrDefault()?.TryGetLocalPath();
            if (path is null) return;
            _journal = new FileRevisionJournal(path);
            _status.Text = "Журнал подключён: " + path;
            await RefreshAsync();
        }
        catch (Exception ex) { if (!_disposed) _status.Text = "Папка журнала недоступна: " + ex.Message; }
    }

    private async Task RefreshAsync()
    {
        if (_disposed || _journal is null || _busy) return;
        _busy = true;
        UpdateButtons();
        try
        {
            var snapshot = await Task.Run(() => _journal.Read(_originalPath));
            if (_disposed) return;
            _current = snapshot.Revisions;
            _revisions.ItemsSource = _current.Select(v =>
                v.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm") +
                " · " + v.Label + " · " + Path.GetFileName(v.RevisionPath)).ToArray();
            _comments.ItemsSource = snapshot.Comments.Select(c =>
                c.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm") +
                " · " + c.Author + ": " + c.Body).ToArray();
            _status.Text = $"Редакций: {_current.Count}; комментариев: {snapshot.Comments.Count}.";
        }
        catch (Exception ex) { if (!_disposed) _status.Text = "Ошибка чтения журнала: " + ex.Message; }
        finally { _busy = false; UpdateButtons(); }
    }

    private async Task RegisterAsync()
    {
        if (_disposed || _journal is null || _busy) return;
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null) return;
        try
        {
            var selected = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Выбрать существующую сохранённую редакцию",
                AllowMultiple = false
            });
            string? path = selected.FirstOrDefault()?.TryGetLocalPath();
            if (path is null) return;
            _busy = true;
            UpdateButtons();
            var journal = _journal;
            await Task.Run(() => journal.RecordRevision(_originalPath, path,
                "Редакция " + DateTimeOffset.Now.ToString("dd.MM.yyyy HH:mm")));
            _busy = false;
            await RefreshAsync();
        }
        catch (Exception ex) { if (!_disposed) _status.Text = "Не удалось зарегистрировать: " + ex.Message; }
        finally { _busy = false; UpdateButtons(); }
    }

    private async Task VerifyAsync()
    {
        var selected = SelectedRevision;
        if (_disposed || _journal is null || selected is null || _busy) return;
        _busy = true;
        UpdateButtons();
        try
        {
            bool valid = await Task.Run(() => _journal.Verify(selected));
            if (!_disposed) _status.Text = valid
                ? "Контрольная сумма совпала с записанной."
                : "Внимание: файл отсутствует либо его содержимое изменилось!";
        }
        catch (Exception ex) { if (!_disposed) _status.Text = "Проверка не выполнена: " + ex.Message; }
        finally { _busy = false; UpdateButtons(); }
    }

    private async Task CompareAsync()
    {
        var selected = SelectedRevision;
        if (_disposed || selected is null || _busy) return;
        _busy = true;
        UpdateButtons();
        try
        {
            var diffs = await Task.Run(() =>
                RevisionTextDiff.Compare(ReadComparableText(_originalPath),
                    ReadComparableText(selected.RevisionPath)));
            if (_disposed) return;
            _comparison.Text = string.Join("\n", diffs.Take(150).Select(line =>
                (line.Kind switch
                {
                    RevisionLineKind.Inserted => "+ ",
                    RevisionLineKind.Deleted => "- ",
                    RevisionLineKind.Modified => "~ ",
                    _ => "  "
                }) + line.Text));
            _status.Text = diffs.Count > 150
                ? "Показаны первые 150 строк различий."
                : "Сравнение текстового содержимого выполнено.";
        }
        catch (Exception ex) { if (!_disposed) _status.Text = "Сравнение недоступно: " + ex.Message; }
        finally { _busy = false; UpdateButtons(); }
    }

    private static string ReadComparableText(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".docx")
            return SimpleDocxTextService.Open(path).Text; // rejects complex DOCX.
        if (ext is not (".txt" or ".md"))
            throw new NotSupportedException("Сравнение доступно только для TXT, MD и простых DOCX.");
        if (new FileInfo(path).Length > 512 * 1024)
            throw new InvalidDataException("Текстовый файл слишком велик для сравнения.");
        return File.ReadAllText(path);
    }

    private async Task AddCommentAsync()
    {
        if (_disposed || _journal is null || _busy) return;
        _busy = true;
        UpdateButtons();
        try
        {
            string author = _author.Text ?? "";
            string body = _comment.Text ?? "";
            Guid target = SelectedRevision?.Id ?? Guid.Empty;
            await Task.Run(() => _journal.AddComment(_originalPath, target, author, body));
            if (_disposed) return;
            _comment.Text = "";
            _busy = false;
            await RefreshAsync();
        }
        catch (Exception ex) { if (!_disposed) _status.Text = "Комментарий не добавлен: " + ex.Message; }
        finally { _busy = false; UpdateButtons(); }
    }

    public void Dispose() => _disposed = true;
}
