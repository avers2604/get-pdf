using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using GetDocument.Core.EasterEgg;
using GetDocument.Core.Workspace;
using GetDocument.Desktop.Preview;
using GetDocument.Desktop.Files;
using GetDocument.Desktop.Templates;
using GetDocument.Desktop.Revisions;
using GetDocument.Office;

namespace GetDocument.Desktop;

/// <summary>
/// Cross-platform first-pass shell. Real document rendering/editing/printing
/// will be connected through adapters; unsupported actions are never presented
/// as successful saves. All known document formats share ONE tab strip.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly DocumentWorkspace _workspace = new();
    private readonly HiddenGameGesture _hiddenGame = new();
    private readonly ObservableCollection<TabItem> _items = [];
    private readonly Dictionary<Guid, TabItem> _views = [];
    private readonly TabControl _tabs = new();
    private readonly TextBlock _status = new();
    private TabItem? _gameTab;
    private TabItem? _filesTab;
    private TabItem? _templatesTab;
    private readonly Dictionary<Guid, TabItem> _revisionTabs = [];

    public MainWindow()
    {
        Title = "ГЭТ Документ";
        Width = 1320;
        Height = 850;
        MinWidth = 940;
        MinHeight = 620;

        var root = new DockPanel();
        var header = BuildHeader();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var statusBar = new Border
        {
            Padding = new Thickness(16, 8),
            Background = new SolidColorBrush(Color.Parse("#E7EDF5")),
            Child = _status
        };
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(statusBar);
        _status.Text = "ГЭТ Документ — прототип общего рабочего пространства";

        _items.Add(new TabItem
        {
            Header = "Главная",
            Content = CreateHome()
        });
        _tabs.ItemsSource = _items;
        _tabs.SelectedIndex = 0;
        _tabs.SelectionChanged += (_, _) =>
        {
            if (_tabs.SelectedItem is TabItem tab && tab.Tag is Guid id)
                _workspace.Select(id);
        };
        root.Children.Add(_tabs);
        Content = root;
        KeyDown += OnKeyDown;
        Closed += (_, _) =>
        {
            if (_filesTab?.Content is IDisposable files) files.Dispose();
            foreach (var history in _revisionTabs.Values)
                if (history.Content is IDisposable pane) pane.Dispose();
        };
        Closing += (_, args) =>
        {
            if (_views.Values.Any(t => t.Content is SimpleDocxEditorPane { HasUnsavedChanges: true } or
                SimpleXlsxEditPane { HasUnsavedChanges: true } or
                ImagePreviewPane { HasUnsavedChanges: true }))
            {
                args.Cancel = true;
                _status.Text = "Есть несохранённые правки документа. Сохраните новую редакцию или отмените изменения.";
            }
        };
    }

    private Control BuildHeader()
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(14, 10)
        };
        // Temporary text target; use official brandbook mark asset after visual acceptance.
        // Reuse the verified official mark from the existing GetPdf asset without altering it.
        Bitmap logo;
        using (var stream = AssetLoader.Open(new Uri("avares://GetDocument.Desktop/Assets/get-mark.png")))
            logo = new Bitmap(stream);
        var mark = new Button
        {
            Content = new Image { Source = logo, Width = 34, Height = 34 },
            Padding = new Thickness(7, 3)
        };
        ToolTip.SetTip(mark, "ГЭТ");
        mark.Click += (_, _) =>
        {
            if (_hiddenGame.Tap(DateTimeOffset.UtcNow)) OpenHiddenGame();
        };
        bar.Children.Add(mark);
        bar.Children.Add(new TextBlock
        {
            Text = "ГЭТ Документ",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var open = new Button { Content = "Открыть файл" };
        open.Click += async (_, _) => await OpenFilesAsync();
        bar.Children.Add(open);
        var getFiles = new Button { Content = "ГЭТ Файлы" };
        getFiles.Click += (_, _) => ShowGetFiles();
        bar.Children.Add(getFiles);
        var templates = new Button { Content = "Шаблоны" };
        templates.Click += (_, _) => ShowCorporateTemplates();
        bar.Children.Add(templates);
        var history = new Button { Content = "История и комментарии" };
        history.Click += (_, _) => ShowRevisionHistory();
        bar.Children.Add(history);
        var home = new Button { Content = "Главная" };
        home.Click += (_, _) => _tabs.SelectedIndex = 0;
        bar.Children.Add(home);
        var print = new Button { Content = "Печать" };
        print.Click += (_, _) => _status.Text =
            "Печать в новом интерфейсе пока не подключена; действующий WPF-просмотрщик не изменён.";
        bar.Children.Add(print);
        return bar;
    }

    private Control CreateHome()
    {
        var content = new StackPanel
        {
            Margin = new Thickness(32),
            Spacing = 16,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        content.Children.Add(new TextBlock
        {
            Text = "Создать документ",
            FontSize = 28,
            FontWeight = FontWeight.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = "Все открытые файлы — в верхних вкладках одного окна.\n" +
                "Новые документы можно сохранить в DOCX, XLSX и PPTX.\n" +
                "Полный офисный редактор подключается следующим этапом.",
            FontSize = 14
        });
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(CreateAction("Текстовый документ", "DOCX",
            () => AddDocument(_workspace.CreateDraft(WorkspaceDocumentKind.Text))));
        actions.Children.Add(CreateAction("Таблица", "XLSX",
            () => AddDocument(_workspace.CreateDraft(WorkspaceDocumentKind.Spreadsheet))));
        actions.Children.Add(CreateAction("Презентация", "PPTX",
            () => AddDocument(_workspace.CreateDraft(WorkspaceDocumentKind.Presentation))));
        actions.Children.Add(CreateAction("Открыть", "PDF, Office, изображения",
            () => _ = OpenFilesAsync()));
        actions.Children.Add(CreateAction("ГЭТ Файлы", "Сетевые папки и документы",
            ShowGetFiles));
        actions.Children.Add(CreateAction("Шаблоны документов", "Записка, акт, письмо",
            ShowCorporateTemplates));
        actions.Children.Add(CreateAction("ГЭТ Сканирование", "Подключение позднее",
            () => OpenPlaceholder("ГЭТ Сканирование",
                "Интеграция с отдельным приложением ГЭТ Сканирование предусмотрена, но пока не подключена.")));
        content.Children.Add(actions);
        content.Children.Add(new TextBlock
        {
            Text = "ГЭТ Файлы: подключение к доступным общим папкам Windows и Linux, без второго хранилища.",
            Margin = new Thickness(0, 26, 0, 0)
        });
        return new ScrollViewer { Content = content };
    }

    private static Button CreateAction(string title, string subtitle, Action action)
    {
        var label = new StackPanel { Spacing = 8, Margin = new Thickness(8) };
        label.Children.Add(new TextBlock { Text = title, FontSize = 16,
            FontWeight = FontWeight.SemiBold });
        label.Children.Add(new TextBlock { Text = subtitle, FontSize = 12 });
        var button = new Button
        {
            Content = label,
            Width = 225,
            Height = 116,
            Margin = new Thickness(0, 0, 12, 12)
        };
        button.Click += (_, _) => action();
        return button;
    }

    private void AddDocument(WorkspaceDocumentTab item)
    {
        if (!_views.TryGetValue(item.Id, out var view))
        {
            Control panel = PreviewFactory.Create(item, SaveDraftAsync, OpenSavedCopyAsync, SaveXlsxRevisionAsync);

            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            title.Children.Add(new TextBlock { Text = item.Title, VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 210, TextTrimming = TextTrimming.CharacterEllipsis });
            var close = new Button { Content = "×", Padding = new Thickness(6, 0) };
            close.Click += (_, _) => CloseDocument(item.Id);
            title.Children.Add(close);
            view = new TabItem { Tag = item.Id, Header = title, Content = panel };
            _views[item.Id] = view;
            _items.Add(view);
        }
        _tabs.SelectedItem = view;
        _status.Text = item.IsDraft ? "Создана вкладка черновика: " + item.Title
            : "Файл добавлен во вкладку: " + item.Title;
    }

    private async Task SaveDraftAsync(WorkspaceDocumentTab item)
    {
        string extension = item.Kind switch
        {
            WorkspaceDocumentKind.Text => "docx",
            WorkspaceDocumentKind.Spreadsheet => "xlsx",
            WorkspaceDocumentKind.Presentation => "pptx",
            _ => throw new NotSupportedException("Нельзя создать файл этого типа.")
        };
        var fileType = new FilePickerFileType(extension.ToUpperInvariant())
        {
            Patterns = ["*." + extension]
        };
        IStorageFile? chosen;
        try
        {
            chosen = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Создать " + item.Title,
                SuggestedFileName = item.Title + "." + extension,
                DefaultExtension = extension,
                ShowOverwritePrompt = false,
                FileTypeChoices = [fileType]
            });
        }
        catch (Exception ex)
        {
            _status.Text = "Ошибка выбора файла: " + ex.Message;
            return;
        }
        if (chosen is null) return;
        if (!chosen.Path.IsFile)
        {
            _status.Text = "Сохранение возможно только в локальную или подключённую сетевую папку.";
            return;
        }

        var target = chosen.Path.LocalPath;
        try
        {
            // Both local and SMB paths preserve existing files, including WORM originals.
            await Task.Run(() => OfficeDocumentFactory.CreateNew(item.Kind, target));
            var saved = _workspace.BindSavedDraft(item.Id, target);
            if (_views.TryGetValue(item.Id, out var tab))
            {
                if (tab.Header is StackPanel title && title.Children.FirstOrDefault() is TextBlock caption)
                    caption.Text = saved.Title;
                tab.Content = PreviewFactory.Create(saved, SaveDraftAsync, OpenSavedCopyAsync, SaveXlsxRevisionAsync);
            }
            _status.Text = "Создан новый офисный файл: " + saved.Title;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or InvalidOperationException)
        {
            _status.Text = "Не удалось сохранить документ: " + ex.Message;
        }
    }

    private Task OpenSavedCopyAsync(string savedPath)
    {
        AddDocument(_workspace.OpenExisting(savedPath));
        return Task.CompletedTask;
    }

    private async Task<bool> SaveXlsxRevisionAsync(
        WorkspaceDocumentTab tab, string expectedSha256, string[][] values)
    {
        if (tab.FilePath is null || !Path.GetExtension(tab.FilePath)
                .Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = "Сохранение возможно только для уже открытого XLSX.";
            return false;
        }

        IStorageFile? chosen;
        try
        {
            chosen = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Сохранить новую редакцию XLSX",
                SuggestedFileName = Path.GetFileNameWithoutExtension(tab.FilePath) + "-редакция.xlsx",
                DefaultExtension = "xlsx",
                ShowOverwritePrompt = false,
                FileTypeChoices = [new FilePickerFileType("XLSX") { Patterns = ["*.xlsx"] }]
            });
        }
        catch (Exception ex)
        {
            _status.Text = "Не удалось открыть диалог сохранения: " + ex.Message;
            return false;
        }
        if (chosen is null) return false;
        if (!chosen.Path.IsFile)
        {
            _status.Text = "Выберите файловую папку или доступный сетевой каталог.";
            return false;
        }

        try
        {
            string destination = chosen.Path.LocalPath;
            await Task.Run(() => SimpleXlsxEditor.SaveNewRevision(
                tab.FilePath, expectedSha256, destination, values));
            await OpenSavedCopyAsync(destination);
            _status.Text = "Создана новая редакция XLSX. Оригинал не изменён.";
            return true;
        }
        catch (Exception ex)
        {
            _status.Text = "Не удалось сохранить XLSX: " + ex.Message;
            return false;
        }
    }

    private void CloseDocument(Guid id)
    {
        if (!_views.TryGetValue(id, out var view)) return;
        if (view.Content is SimpleDocxEditorPane { HasUnsavedChanges: true } or
            SimpleXlsxEditPane { HasUnsavedChanges: true } or
            ImagePreviewPane { HasUnsavedChanges: true })
        {
            _status.Text = "Есть несохранённые изменения. Сохраните редакцию или отмените правки.";
            return;
        }
        _views.Remove(id);
        _workspace.Close(id);
        if (view.Content is IDisposable disposable) disposable.Dispose();
        _items.Remove(view);
        if (_tabs.SelectedItem is null || ReferenceEquals(_tabs.SelectedItem, view))
        {
            if (_workspace.ActiveId is { } active && _views.TryGetValue(active, out var selected))
                _tabs.SelectedItem = selected;
            else _tabs.SelectedIndex = 0;
        }
    }

    /// <summary>
    /// Opens trusted local/SMB paths within the current tab strip. The caller
    /// has only local OS-user IPC permissions, never GUP-2 credentials.
    /// Actual parsers must still inspect the bytes and enforce limits.
    /// </summary>
    public void OpenFromLaunch(IReadOnlyList<string> files)
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();

        foreach (string path in files)
        {
            try { AddDocument(_workspace.OpenExisting(path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
            {
                // No paths in log/IPC errors: failures are shown only to
                // the user who initiated the local launch.
                _status.Text = "Не удалось открыть переданный файл: " + ex.Message;
            }
        }
    }

    public void ReportRejectedLaunch() =>
        _status.Text = "Неверные параметры запуска: допустимы только локальные файлы поддерживаемых форматов.";

    private async Task OpenFilesAsync()
    {
        IReadOnlyList<IStorageFile> files;
        try
        {
            files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Открыть документ",
                AllowMultiple = true
            });
        }
        catch (Exception ex)
        {
            _status.Text = "Не удалось открыть диалог: " + ex.Message;
            return;
        }
        foreach (var file in files)
        {
            if (!file.Path.IsFile)
            {
                _status.Text = "Требуется доступный локальный файл или подключённая сетевая папка.";
                continue;
            }
            try { AddDocument(_workspace.OpenExisting(file.Path.LocalPath)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
            { _status.Text = "Не удалось открыть документ: " + ex.Message; }
        }
    }

    private void OpenPlaceholder(string title, string explanation)
    {
        var tab = new TabItem
        {
            Header = title,
            Content = new TextBlock { Margin = new Thickness(32), Text = explanation,
                TextWrapping = TextWrapping.Wrap }
        };
        _items.Add(tab);
        _tabs.SelectedItem = tab;
    }

    private void ShowGetFiles()
    {
        if (_filesTab is not null)
        {
            _tabs.SelectedItem = _filesTab;
            return;
        }

        var pane = new GetFilesPane(path =>
        {
            try { AddDocument(_workspace.OpenExisting(path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
            {
                _status.Text = "Не удалось открыть документ из ГЭТ Файлы: " + ex.Message;
            }
        });
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        heading.Children.Add(new TextBlock
        {
            Text = "ГЭТ Файлы",
            VerticalAlignment = VerticalAlignment.Center
        });
        var close = new Button { Content = "×", Padding = new Thickness(6, 0) };
        close.Click += (_, _) => CloseGetFiles();
        heading.Children.Add(close);
        _filesTab = new TabItem { Header = heading, Content = pane };
        _items.Add(_filesTab);
        _tabs.SelectedItem = _filesTab;
    }

    private void CloseGetFiles()
    {
        if (_filesTab is null) return;
        if (_filesTab.Content is IDisposable disposable) disposable.Dispose();
        _items.Remove(_filesTab);
        _filesTab = null;
        _tabs.SelectedIndex = 0;
    }

    private void ShowCorporateTemplates()
    {
        if (_templatesTab is not null)
        {
            _tabs.SelectedItem = _templatesTab;
            return;
        }
        var content = new CorporateTemplatesPane(path =>
        {
            AddDocument(_workspace.OpenExisting(path));
            return Task.CompletedTask;
        });
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        heading.Children.Add(new TextBlock
        {
            Text = "Шаблоны",
            VerticalAlignment = VerticalAlignment.Center
        });
        var close = new Button { Content = "×", Padding = new Thickness(6, 0) };
        close.Click += (_, _) => CloseCorporateTemplates();
        heading.Children.Add(close);
        _templatesTab = new TabItem { Header = heading, Content = content };
        _items.Add(_templatesTab);
        _tabs.SelectedItem = _templatesTab;
    }

    private void CloseCorporateTemplates()
    {
        if (_templatesTab is null) return;
        _items.Remove(_templatesTab);
        _templatesTab = null;
        _tabs.SelectedIndex = 0;
    }

    private void ShowRevisionHistory()
    {
        var source = _workspace.Tabs.FirstOrDefault(t => t.Id == _workspace.ActiveId);
        if (source?.FilePath is not { } filePath)
        {
            _status.Text = "Для истории сначала откройте и выберите сохранённый документ.";
            return;
        }
        if (_revisionTabs.TryGetValue(source.Id, out var existing))
        {
            _tabs.SelectedItem = existing;
            return;
        }
        var pane = new RevisionHistoryPane(filePath, saved =>
        {
            try { AddDocument(_workspace.OpenExisting(saved)); }
            catch (Exception ex)
            {
                _status.Text = "Не удалось открыть редакцию во вкладке: " + ex.Message;
            }
        });
        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5
        };
        heading.Children.Add(new TextBlock
        {
            Text = "История: " + source.Title,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 210,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var close = new Button { Content = "×", Padding = new Thickness(6, 0) };
        Guid sourceId = source.Id;
        close.Click += (_, _) => CloseRevisionHistory(sourceId);
        heading.Children.Add(close);
        var view = new TabItem { Header = heading, Content = pane };
        _revisionTabs[source.Id] = view;
        _items.Add(view);
        _tabs.SelectedItem = view;
    }

    private void CloseRevisionHistory(Guid sourceId)
    {
        if (!_revisionTabs.Remove(sourceId, out var tab)) return;
        if (tab.Content is IDisposable disposable) disposable.Dispose();
        _items.Remove(tab);
        _tabs.SelectedIndex = 0;
    }

    private void OpenHiddenGame()
    {
        if (_gameTab is not null)
        {
            _tabs.SelectedItem = _gameTab;
            return;
        }
        // Intentional intranet-only placeholder. No socket, external URL, telemetry or credentials.
        var content = new StackPanel { Margin = new Thickness(32), Spacing = 14 };
        content.Children.Add(new TextBlock
        {
            Text = "Дурак — внутренняя игра",
            FontSize = 24,
            FontWeight = FontWeight.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = "Скрытый модуль открыт. Онлайн-партии станут доступны после " +
                "подключения разрешённого внутреннего игрового сервера. " +
                "В текущем прототипе сетевые соединения не выполняются.",
            TextWrapping = TextWrapping.Wrap
        });
        var close = new Button { Content = "Закрыть" };
        close.Click += (_, _) =>
        {
            if (_gameTab is not null) _items.Remove(_gameTab);
            _gameTab = null;
            _tabs.SelectedIndex = 0;
        };
        content.Children.Add(close);
        _gameTab = new TabItem { Header = "Игра", Content = content };
        _items.Add(_gameTab);
        _tabs.SelectedItem = _gameTab;
        _status.Text = "Скрытый модуль открыт. Онлайн-сервер не настроен.";
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;
        if (e.Key == Key.O) { e.Handled = true; await OpenFilesAsync(); }
        else if (e.Key == Key.T)
        {
            e.Handled = true;
            AddDocument(_workspace.CreateDraft(WorkspaceDocumentKind.Text));
        }
        else if (e.Key == Key.W && _tabs.SelectedItem is TabItem { Tag: Guid id })
        { e.Handled = true; CloseDocument(id); }
        else if (e.Key == Key.W && ReferenceEquals(_tabs.SelectedItem, _filesTab))
        { e.Handled = true; CloseGetFiles(); }
        else if (e.Key == Key.W && ReferenceEquals(_tabs.SelectedItem, _templatesTab))
        { e.Handled = true; CloseCorporateTemplates(); }
        else if (e.Key == Key.W)
        {
            var pair = _revisionTabs.FirstOrDefault(kv => ReferenceEquals(kv.Value, _tabs.SelectedItem));
            if (pair.Value is not null)
            {
                e.Handled = true;
                CloseRevisionHistory(pair.Key);
            }
        }
    }
}
