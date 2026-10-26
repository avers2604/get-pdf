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
using GetDocument.Core.Collaboration;
using GetDocument.Desktop.Preview;
using GetDocument.Desktop.Files;
using GetDocument.Desktop.Templates;
using GetDocument.Desktop.Revisions;
using GetDocument.Desktop.Collaboration;
using GetDocument.Office;
using GetDocument.Core.Integration;
using GetDocument.Desktop.Integration;
using GetDocument.Desktop.Printing;

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
    private readonly Dictionary<string, TabItem> _collaborationTabs = new(StringComparer.Ordinal);
    private readonly OfficeReturnPolicy _officeReturnPolicy = OfficeReturnPolicy.LoadLocal();
    private TabItem? _officeReturnTab;
    private TabItem? _printTab;
    private readonly DocumentHistory _history = new();
    private readonly StackPanel _homeLists = new() { Spacing = 4 };
    private bool _closeConfirmed;

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
            Padding = new Thickness(20, 9),
            Child = _status
        };
        statusBar.Classes.Add("statusbar");
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        Avalonia.Automation.AutomationProperties.SetName(_status, "Строка состояния");
        Avalonia.Automation.AutomationProperties.SetLiveSetting(_status,
            Avalonia.Automation.AutomationLiveSetting.Polite);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(statusBar);
        _status.Text = "Готово. Откройте файл или создайте новый документ.";

        _items.Add(new TabItem
        {
            Header = "Главная",
            Content = CreateHome()
        });
        _tabs.ItemsSource = _items;
        // Screen readers announce the tab caption (the header is a panel with a close button).
        _items.CollectionChanged += (_, e) =>
        {
            foreach (var tab in e.NewItems?.OfType<TabItem>() ?? [])
                if (tab.Header is StackPanel { Children: [TextBlock caption, ..] })
                    Avalonia.Automation.AutomationProperties.SetName(tab, caption.Text);
        };
        _tabs.SelectedIndex = 0;
        _tabs.SelectionChanged += (_, _) =>
        {
            if (_tabs.SelectedItem is TabItem tab && tab.Tag is Guid id)
                _workspace.Select(id);
            if (_tabs.SelectedIndex == 0) RefreshHome();
        };
        try { _history.ImportFromGetPdfOnce(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        RefreshHome();
        root.Children.Add(_tabs);
        Content = root;
        KeyDown += OnKeyDown;
        Closed += (_, _) =>
        {
            if (_filesTab?.Content is IDisposable files) files.Dispose();
            if (_officeReturnTab?.Content is IDisposable officeReturn) officeReturn.Dispose();
            if (_printTab?.Content is IDisposable printWizard) printWizard.Dispose();
            foreach (var view in _views.Values)
                if (view.Content is IDisposable document) document.Dispose();
            foreach (var history in _revisionTabs.Values)
                if (history.Content is IDisposable pane) pane.Dispose();
            foreach (var editTab in _collaborationTabs.Values)
                if (editTab.Content is IDisposable editor) editor.Dispose();
        };
        Closing += async (_, args) =>
        {
            if (_closeConfirmed) return;
            var unsaved = _views.Values.Where(t => HasUnsaved(t.Content)).ToList();
            if (unsaved.Count == 0) return;
            args.Cancel = true;
            string names = string.Join(", ", unsaved.Select(t => (t.Header as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "документ").Take(4));
            if (await GetDialogs.ConfirmAsync(this, "Закрыть без сохранения?",
                    $"Есть несохранённые изменения: {names}. Если закрыть программу, они пропадут. Исходные файлы не изменены.",
                    "Закрыть без сохранения", "Вернуться"))
            {
                _closeConfirmed = true;
                Close();
            }
        };
    }

    private static bool HasUnsaved(object? content) => content is SimpleDocxEditorPane { HasUnsavedChanges: true } or
        SimpleXlsxEditPane { HasUnsavedChanges: true } or ImagePreviewPane { HasUnsavedChanges: true } or
        GetDocument.Desktop.Pdf.PdfWorkspacePane { HasUnsavedChanges: true };

    /// <summary>Favourites and recent documents on the home screen (all formats).</summary>
    private void RefreshHome()
    {
        _homeLists.Children.Clear();
        IReadOnlyList<string> favorites;
        IReadOnlyList<RecentDocument> recent;
        try { favorites = _history.Favorites(); recent = _history.Recent(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        var favoriteSet = new HashSet<string>(favorites, StringComparer.OrdinalIgnoreCase);
        if (favorites.Count > 0)
        {
            _homeLists.Children.Add(Section("Избранное"));
            foreach (string path in favorites) _homeLists.Children.Add(HistoryRow(path, null, true, recentRow: false));
        }
        var recentHeader = new DockPanel { Margin = new Thickness(0, 18, 0, 2) };
        if (recent.Count > 0)
        {
            var clear = new Button { Content = "Очистить историю" };
            clear.Classes.Add("ghost");
            clear.Click += async (_, _) =>
            {
                if (!await GetDialogs.ConfirmAsync(this, "Очистить историю?",
                        "Список недавних документов будет удалён. Сами файлы и избранное не затрагиваются.", "Очистить")) return;
                _history.ClearRecent();
                RefreshHome();
            };
            DockPanel.SetDock(clear, Dock.Right);
            recentHeader.Children.Add(clear);
        }
        var title = new TextBlock { Text = "Недавние документы" };
        title.Classes.Add("section");
        recentHeader.Children.Add(title);
        _homeLists.Children.Add(recentHeader);
        if (recent.Count == 0)
        {
            var empty = new TextBlock { Text = "Здесь появятся документы, которые вы открывали." };
            empty.Classes.Add("hint");
            _homeLists.Children.Add(empty);
        }
        foreach (var item in recent)
            _homeLists.Children.Add(HistoryRow(item.Path, item.OpenedUtc, favoriteSet.Contains(item.Path), recentRow: true));
    }

    private Control HistoryRow(string path, DateTime? opened, bool favorite, bool recentRow)
    {
        bool exists = File.Exists(path);
        var kind = GetDocument.Core.DocumentFiles.DocumentFileClassifier.Classify(path).Kind;
        var icon = kind switch
        {
            GetDocument.Core.DocumentFiles.DocumentFileKind.Spreadsheet => Icons.Table,
            GetDocument.Core.DocumentFiles.DocumentFileKind.Presentation => Icons.Presentation,
            GetDocument.Core.DocumentFiles.DocumentFileKind.Image => Icons.Square,
            _ => Icons.FileText
        };
        string folder = Path.GetDirectoryName(path) ?? "";
        string when = opened is { } at ? DescribeOpened(at.ToLocalTime(), DateTime.Now) + " · " : "";
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = Path.GetFileName(path), FontWeight = FontWeight.SemiBold,
            Foreground = (IBrush)Application.Current!.FindResource("GetNavyBrush")!, TextTrimming = TextTrimming.CharacterEllipsis });
        var detail = new TextBlock { Text = (exists ? when : "Файл не найден · ") + folder, TextTrimming = TextTrimming.CharacterEllipsis };
        detail.Classes.Add("hint");
        text.Children.Add(detail);
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        line.Children.Add(Icons.Create(icon, 22, "GetTealBrush"));
        line.Children.Add(text);
        var open = new Button { Content = line, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, MinHeight = 52, Opacity = exists ? 1 : .55 };
        open.Classes.Add("ghost");
        Avalonia.Automation.AutomationProperties.SetName(open, exists ? "Открыть " + Path.GetFileName(path) : Path.GetFileName(path) + ": файл не найден");
        ToolTip.SetTip(open, path);
        open.Click += async (_, _) =>
        {
            if (!File.Exists(path))
            {
                if (await GetDialogs.ConfirmAsync(this, "Документ не найден",
                        "Файл переместили, переименовали или сетевая папка недоступна. Убрать его из списка?", "Убрать"))
                {
                    if (recentRow) _history.Forget(path); else _history.ToggleFavorite(path);
                    RefreshHome();
                }
                return;
            }
            OpenFromLaunch([path]);
        };
        var star = new Button { Content = Icons.Create(Icons.Star, 18), Width = 40, MinHeight = 40, Padding = new Thickness(0) };
        star.Classes.Add("ghost");
        if (favorite && star.Content is Viewbox { Child: Canvas { Children: [Avalonia.Controls.Shapes.Path p] } })
        {
            p.Fill = new SolidColorBrush(Color.Parse("#FBBA00"));
            p.Stroke = new SolidColorBrush(Color.Parse("#C98F00"));
        }
        string starTip = favorite ? "Убрать из избранного" : "Добавить в избранное";
        ToolTip.SetTip(star, starTip);
        Avalonia.Automation.AutomationProperties.SetName(star, starTip);
        star.Click += (_, _) => { ToggleFavorite(path); RefreshHome(); };
        var row = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        actions.Children.Add(star);
        // Favourite rows keep an invisible placeholder so the stars of both lists line up.
        var remove = new Button { Content = Icons.Create(Icons.X, 16), Width = 40, MinHeight = 40, Padding = new Thickness(0),
            Opacity = recentRow ? 1 : 0, IsHitTestVisible = recentRow, Focusable = recentRow };
        remove.Classes.Add("ghost");
        ToolTip.SetTip(remove, "Убрать из истории (файл не удаляется)");
        Avalonia.Automation.AutomationProperties.SetName(remove, "Убрать из истории");
        remove.Click += (_, _) => { _history.Forget(path); RefreshHome(); };
        actions.Children.Add(remove);
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);
        row.Children.Add(open);
        return row;
    }

    internal static string DescribeOpened(DateTime local, DateTime now)
    {
        var ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        if (local.Date == now.Date) return "сегодня, " + local.ToString("HH:mm", ru);
        if (local.Date == now.Date.AddDays(-1)) return "вчера, " + local.ToString("HH:mm", ru);
        return local.Year == now.Year ? local.ToString("d MMM, HH:mm", ru) : local.ToString("d MMM yyyy", ru);
    }

    private bool ToggleFavorite(string path)
    {
        try
        {
            bool now = _history.ToggleFavorite(path);
            _status.Text = now ? "Документ добавлен в избранное." : "Документ убран из избранного.";
            foreach (var tab in _views.Values)
                if (tab.Content is GetDocument.Desktop.Pdf.PdfWorkspacePane pdf &&
                    string.Equals(pdf.OriginalPath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                    pdf.SetFavorite(now);
            return now;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _status.Text = ex.Message;
            return _history.IsFavorite(path);
        }
    }

    private Control BuildHeader()
    {
        // GET DS 4.1 shell header: official mark + product name, then commands. The mark is
        // the brandbook asset (no button chrome); it still counts taps for the hidden module.
        var bar = new DockPanel { Margin = new Thickness(20, 10), LastChildFill = false };
        Bitmap logo;
        using (var stream = AssetLoader.Open(new Uri("avares://GetDocument.Desktop/Assets/get-mark.png")))
            logo = new Bitmap(stream);
        var mark = new Button
        {
            Content = new Image { Source = logo, Width = 36, Height = 36 },
            Padding = new Thickness(2),
            MinHeight = 0
        };
        mark.Classes.Add("ghost");
        Avalonia.Automation.AutomationProperties.SetName(mark, "Знак ГЭТ");
        ToolTip.SetTip(mark, "ГЭТ Документ");
        mark.Click += (_, _) =>
        {
            if (_hiddenGame.Tap(DateTimeOffset.UtcNow)) OpenHiddenGame();
        };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(mark);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = "ГЭТ Документ", FontSize = 18, FontWeight = FontWeight.SemiBold,
            Foreground = (IBrush)Application.Current!.FindResource("GetNavyBrush")! });
        names.Children.Add(new TextBlock { Text = "PDF, Office и изображения в одном окне", FontSize = 12,
            Foreground = (IBrush)Application.Current!.FindResource("GetMutedBrush")! });
        brand.Children.Add(names);
        DockPanel.SetDock(brand, Dock.Left);
        bar.Children.Add(brand);

        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
            Margin = new Thickness(28, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        commands.Children.Add(HeaderCommand("Главная", Icons.Home, "Главная (стартовый экран)",
            () => _tabs.SelectedIndex = 0));
        commands.Children.Add(HeaderCommand("ГЭТ Файлы", Icons.Network, "Сетевые папки",
            ShowGetFiles));
        commands.Children.Add(HeaderCommand("Шаблоны", Icons.Template, "Корпоративные шаблоны",
            ShowCorporateTemplates));
        commands.Children.Add(HeaderCommand("История", Icons.History,
            "История редакций и комментарии выбранного документа", ShowRevisionHistory));
        if (_officeReturnPolicy.Enabled)
            commands.Children.Add(HeaderCommand("Отправить в GUP-2", Icons.Send,
                "Вернуть рабочую копию DOCX/XLSX в карточку GUP-2", ShowOfficeReturn));
        DockPanel.SetDock(commands, Dock.Left);
        bar.Children.Add(commands);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center };
        var print = HeaderCommand("Печать", Icons.Printer, "Печать выбранного документа (Ctrl+P)", ShowPrintWizard);
        print.Classes.Remove("ghost");
        print.Classes.Add("toolbar");
        right.Children.Add(print);
        var open = HeaderCommand("Открыть файл", Icons.Folder, "Открыть документ (Ctrl+O)",
            () => _ = OpenFilesAsync());
        open.Classes.Remove("ghost");
        open.Classes.Add("primary");
        right.Children.Add(open);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);

        var header = new Border { Child = bar };
        header.Classes.Add("header");
        return header;
    }

    private static Button HeaderCommand(string text, Geometry icon, string tip, Action action)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(Icons.Create(icon, 18));
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, MinHeight = 44 };
        button.Classes.Add("ghost");
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, text);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>DS tab caption with a labelled close button.</summary>
    private static StackPanel TabHeader(string title, Action close)
    {
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        heading.Children.Add(new TextBlock
        {
            Text = title, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        var button = new Button { Content = "×" };
        button.Classes.Add("tabclose");
        ToolTip.SetTip(button, "Закрыть вкладку (Ctrl+W)");
        Avalonia.Automation.AutomationProperties.SetName(button, "Закрыть вкладку " + title);
        button.Click += (_, _) => close();
        heading.Children.Add(button);
        return heading;
    }

    private Control CreateHome()
    {
        var content = new StackPanel
        {
            Margin = new Thickness(40, 32),
            Spacing = 10,
            MaxWidth = 1080,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var title = new TextBlock { Text = "Создайте или откройте документ" };
        title.Classes.Add("title");
        content.Children.Add(title);
        var lead = new TextBlock
        {
            Text = "PDF, документы Word, таблицы Excel, презентации и изображения открываются во вкладках одного окна. " +
                   "Файлы можно перетащить в окно."
        };
        lead.Classes.Add("hint");
        content.Children.Add(lead);

        content.Children.Add(Section("Создать"));
        var create = new WrapPanel { Orientation = Orientation.Horizontal };
        create.Children.Add(CreateAction("Текстовый документ", "Документ Word (DOCX)", Icons.FileText,
            () => AddDocument(_workspace.CreateDraft(WorkspaceDocumentKind.Text))));
        create.Children.Add(CreateAction("Таблица", "Книга Excel (XLSX)", Icons.Table,
            () => AddDocument(_workspace.CreateDraft(WorkspaceDocumentKind.Spreadsheet))));
        create.Children.Add(CreateAction("Презентация", "PowerPoint (PPTX)", Icons.Presentation,
            () => AddDocument(_workspace.CreateDraft(WorkspaceDocumentKind.Presentation))));
        create.Children.Add(CreateAction("По шаблону", "Записка, акт, письмо", Icons.Template,
            ShowCorporateTemplates));
        content.Children.Add(create);

        content.Children.Add(Section("Открыть"));
        var open = new WrapPanel { Orientation = Orientation.Horizontal };
        open.Children.Add(CreateAction("Открыть файл…", "PDF, Office, изображения", Icons.Folder,
            () => _ = OpenFilesAsync()));
        open.Children.Add(CreateAction("ГЭТ Файлы", "Общие сетевые папки", Icons.Network, ShowGetFiles));
        open.Children.Add(CreateAction("ГЭТ Сканирование", ScannerApp.IsInstalled
                ? "Сканировать документы в PDF" : "Приложение не установлено", Icons.Scanner,
            OpenScanner));
        open.Children.Add(CreateAction("Совместное редактирование", "Нужен сервер ГЭТ (настраивает ИТ)",
            Icons.Users,
            () => OpenPlaceholder("Совместная работа",
                "Совместное редактирование в реальном времени работает через сервер Collabora и вход в GUP-2. " +
                "Когда ИТ-служба подключит сервер, документ из карточки GUP-2 откроется здесь, во вкладке. " +
                "Сейчас сервер не настроен: правки и права пользователей не имитируются.")));
        content.Children.Add(open);
        content.Children.Add(_homeLists);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
        return new ScrollViewer { Content = content };
    }

    private static TextBlock Section(string text)
    {
        var block = new TextBlock { Text = text, Margin = new Thickness(0, 18, 0, 2) };
        block.Classes.Add("section");
        return block;
    }

    private static Button CreateAction(string title, string subtitle, Geometry icon, Action action)
    {
        var label = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var badge = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(10),
            Background = (IBrush)Application.Current!.FindResource("GetAccentSoftBrush")!,
            Child = Icons.Create(icon, 22, "GetTealBrush"),
            VerticalAlignment = VerticalAlignment.Top
        };
        label.Children.Add(badge);
        var text = new StackPanel { Spacing = 4, Margin = new Thickness(14, 0, 0, 0) };
        text.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold,
            Foreground = (IBrush)Application.Current!.FindResource("GetNavyBrush")!,
            TextWrapping = TextWrapping.Wrap });
        var sub = new TextBlock { Text = subtitle };
        sub.Classes.Add("hint");
        text.Children.Add(sub);
        Grid.SetColumn(text, 1);
        label.Children.Add(text);
        var button = new Button
        {
            Content = label,
            Width = 252,
            Height = 112,
            Margin = new Thickness(0, 0, 12, 12)
        };
        button.Classes.Add("tile");
        Avalonia.Automation.AutomationProperties.SetName(button, title + ". " + subtitle);
        button.Click += (_, _) => action();
        return button;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;
        OpenFromLaunch(files.Select(f => f.Path).Where(p => p.IsFile).Select(p => p.LocalPath).ToArray());
    }

    private void OpenScanner()
    {
        if (ScannerApp.TryLaunch(out string message)) _status.Text = message;
        else OpenPlaceholder("ГЭТ Сканирование", message);
    }

    private void AddDocument(WorkspaceDocumentTab item)
    {
        if (!_views.TryGetValue(item.Id, out var view))
        {
            Control panel = PreviewFactory.Create(item, SaveDraftAsync, OpenSavedCopyAsync, SaveXlsxRevisionAsync, SetStatus);

            var title = TabHeader(item.Title, () => _ = CloseDocumentAsync(item.Id));
            view = new TabItem { Tag = item.Id, Header = title, Content = panel };
            if (item.FilePath is { } opened)
            {
                try { _history.Remember(opened); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
                if (panel is GetDocument.Desktop.Pdf.PdfWorkspacePane pdf)
                {
                    pdf.SetFavorite(_history.IsFavorite(opened));
                    pdf.FavoriteToggleRequested += _ => ToggleFavorite(opened);
                }
            }
            Avalonia.Automation.AutomationProperties.SetName(view, item.Title);
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
                tab.Content = PreviewFactory.Create(saved, SaveDraftAsync, OpenSavedCopyAsync, SaveXlsxRevisionAsync, SetStatus);
            }
            if (_views.TryGetValue(item.Id, out var created) && created.Content is OfficeDocumentPane office &&
                GetDocument.Office.LibreOffice.LibreOfficeRuntime.IsAvailable)
                office.Edit(); // a new document is meant to be written: open the editor right away
            else
                _status.Text = "Создан новый офисный файл: " + saved.Title;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or InvalidOperationException)
        {
            _status.Text = "Не удалось сохранить документ: " + ex.Message;
        }
    }

    private void SetStatus(string text) => _status.Text = text;

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

    private async Task CloseDocumentAsync(Guid id)
    {
        if (!_views.TryGetValue(id, out var view)) return;
        if (HasUnsaved(view.Content) &&
            !await GetDialogs.ConfirmAsync(this, "Закрыть без сохранения?",
                "В документе есть несохранённые изменения. Если закрыть вкладку, они пропадут. Исходный файл не изменён.",
                "Закрыть без сохранения", "Вернуться"))
            return;
        CloseDocument(id);
    }

    private void CloseDocument(Guid id)
    {
        if (!_views.TryGetValue(id, out var view)) return;
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

    private void ShowOfficeReturn()
    {
        if (_officeReturnTab is not null)
        {
            _tabs.SelectedItem = _officeReturnTab;
            return;
        }
        if (!_officeReturnPolicy.Enabled) return;
        var heading = TabHeader("Возврат GUP-2", CloseOfficeReturn);
        _officeReturnTab = new TabItem
        {
            Header = heading,
            Content = new OfficeReturnPane(this, _officeReturnPolicy)
        };
        _items.Add(_officeReturnTab);
        _tabs.SelectedItem = _officeReturnTab;
    }

    private void CloseOfficeReturn()
    {
        if (_officeReturnTab is null) return;
        if (_officeReturnTab.Content is IDisposable pane) pane.Dispose();
        _items.Remove(_officeReturnTab);
        _officeReturnTab = null;
        _tabs.SelectedIndex = 0;
    }

    private async void ShowPrintWizard()
    {
        if (_tabs.SelectedItem is TabItem { Tag: Guid id })
        {
            var document = _workspace.Tabs.FirstOrDefault(x => x.Id == id);
            string? printable = null;
            if (_views.TryGetValue(id, out var pdfTab) && pdfTab.Content is GetDocument.Desktop.Pdf.PdfWorkspacePane pdf)
            {
                // Prints what is on screen: unsaved stamps, numbers and page edits included.
                printable = await pdf.PreparePrintableAsync();
                if (printable is null) return;
            }
            else if (document?.FilePath is { } path &&
                string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
                printable = path;
            else if (_views.TryGetValue(id, out var officeTab) && officeTab.Content is OfficeDocumentPane office)
            {
                // Word/Excel/PowerPoint print exactly what the preview shows: LibreOffice's PDF.
                printable = office.PreviewPdf;
                if (printable is null)
                {
                    _status.Text = "Документ ещё готовится к просмотру. Повторите печать через несколько секунд.";
                    return;
                }
            }
            if (printable is null || document is null)
            {
                _status.Text = "Печать доступна для PDF и документов Word, Excel, PowerPoint. " +
                    "Изображение сначала сохраните или откройте как PDF.";
                return;
            }
            ClosePrintWizard();
            var heading = TabHeader("Печать: " + document.Title, ClosePrintWizard);
            _printTab = new TabItem
            {
                Header = heading,
                Content = new PrintWizardPane(printable, ClosePrintWizard)
            };
            _items.Add(_printTab);
            _tabs.SelectedItem = _printTab;
            _status.Text = "Открыт мастер печати ГЭТ. Параметры задаются без системного диалога.";
            return;
        }
        if (_printTab is not null)
        {
            _tabs.SelectedItem = _printTab;
            return;
        }
        _status.Text = "Выберите вкладку сохранённого PDF перед печатью.";
    }

    private void ClosePrintWizard()
    {
        if (_printTab is null) return;
        if (_printTab.Content is IDisposable pane) pane.Dispose();
        _items.Remove(_printTab);
        _printTab = null;
        _tabs.SelectedIndex = 0;
    }

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

    /// <summary>
    /// Call ONLY from a trusted, authenticated GUP-2/SSO handoff after
    /// the enterprise launch broker has approved a short-lived session.
    /// This API is internal: raw local paths / CLI / IPC do not grant access.
    /// The same NativeWebView occupies an ordinary document tab.
    /// </summary>
    internal void OpenCollaborationSession(CollaborationLaunchDescriptor launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        string key = launch.LaunchPage.AbsoluteUri;
        if (_collaborationTabs.TryGetValue(key, out var existing))
        {
            _tabs.SelectedItem = existing;
            return;
        }
        var pane = new CollaborationEditorPane(launch);
        var heading = TabHeader("Совместно: " + launch.DisplayName, () => CloseCollaborationSession(key));
        var view = new TabItem { Header = heading, Content = pane };
        _collaborationTabs.Add(key, view);
        _items.Add(view);
        _tabs.SelectedItem = view;
        _status.Text = "Открыта внутренняя совместная вкладка. Подтверждение сохранения выдаёт сервер.";
    }

    private void CloseCollaborationSession(string key)
    {
        if (!_collaborationTabs.Remove(key, out var tab)) return;
        if (tab.Content is IDisposable editor) editor.Dispose();
        _items.Remove(tab);
        _tabs.SelectedIndex = 0;
        _status.Text = "Вкладка закрыта. Убедитесь, что сервер подтвердил сохранение изменений.";
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
        var heading = TabHeader("ГЭТ Файлы", CloseGetFiles);
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
        var heading = TabHeader("Шаблоны", CloseCorporateTemplates);
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
        Guid sourceId = source.Id;
        var heading = TabHeader("История: " + source.Title, () => CloseRevisionHistory(sourceId));
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
        if (_tabs.SelectedItem is TabItem { Content: GetDocument.Desktop.Pdf.PdfWorkspacePane activePdf })
        {
            if (e.Key == Key.F3)
            {
                e.Handled = true;
                activePdf.FindNext(reverse: (e.KeyModifiers & KeyModifiers.Shift) != 0);
                return;
            }
            if (e.Key == Key.F && (e.KeyModifiers & KeyModifiers.Control) != 0)
            {
                e.Handled = true;
                activePdf.FocusSearch();
                return;
            }
            if ((e.KeyModifiers & KeyModifiers.Control) != 0)
            {
                if (e.Key == Key.Add || e.Key == Key.OemPlus)
                {
                    e.Handled = true;
                    activePdf.ChangeZoom(1);
                    return;
                }
                if (e.Key == Key.Subtract || e.Key == Key.OemMinus)
                {
                    e.Handled = true;
                    activePdf.ChangeZoom(-1);
                    return;
                }
                if (e.Key == Key.D0 || e.Key == Key.NumPad0)
                {
                    e.Handled = true;
                    activePdf.ResetZoom();
                    return;
                }
            }
        }
        if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;
        if (e.Key == Key.O) { e.Handled = true; await OpenFilesAsync(); }
        else if (e.Key == Key.P) { e.Handled = true; ShowPrintWizard(); }
        else if (e.Key == Key.G && _tabs.SelectedItem is TabItem { Content: GetDocument.Desktop.Pdf.PdfWorkspacePane pdf })
        { e.Handled = true; pdf.FocusPageNumber(); }
        else if (e.Key == Key.T)
        {
            e.Handled = true;
            AddDocument(_workspace.CreateDraft(WorkspaceDocumentKind.Text));
        }
        else if (e.Key == Key.W && _tabs.SelectedItem is TabItem { Tag: Guid id })
        { e.Handled = true; await CloseDocumentAsync(id); }
        else if (e.Key == Key.W && ReferenceEquals(_tabs.SelectedItem, _filesTab))
        { e.Handled = true; CloseGetFiles(); }
        else if (e.Key == Key.W && ReferenceEquals(_tabs.SelectedItem, _templatesTab))
        { e.Handled = true; CloseCorporateTemplates(); }
        else if (e.Key == Key.W && ReferenceEquals(_tabs.SelectedItem, _officeReturnTab))
        { e.Handled = true; CloseOfficeReturn(); }
        else if (e.Key == Key.W && ReferenceEquals(_tabs.SelectedItem, _printTab))
        { e.Handled = true; ClosePrintWizard(); }
        else if (e.Key == Key.W)
        {
            var pair = _revisionTabs.FirstOrDefault(kv => ReferenceEquals(kv.Value, _tabs.SelectedItem));
            if (pair.Value is not null)
            {
                e.Handled = true;
                CloseRevisionHistory(pair.Key);
                return;
            }
            var collaborative = _collaborationTabs.FirstOrDefault(
                kv => ReferenceEquals(kv.Value, _tabs.SelectedItem));
            if (collaborative.Value is not null)
            {
                e.Handled = true;
                CloseCollaborationSession(collaborative.Key);
            }
        }
    }
}
