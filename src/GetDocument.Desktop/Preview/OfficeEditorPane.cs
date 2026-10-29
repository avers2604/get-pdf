using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GetDocument.Desktop.Office;
using GetDocument.Office.LibreOffice;

namespace GetDocument.Desktop.Preview;

/// <summary>
/// Word / Excel / PowerPoint tab: the document is edited right here. LibreOffice draws the
/// pages inside the tab (its own menus, toolbars and sidebar are switched off); every command
/// is a GET DS control on this pane's toolbar. Ctrl+S, Ctrl+P and similar shortcuts pressed in
/// the document come back here, so saving and printing look and behave like the rest of
/// GET Document (the GET print wizard, GET file dialogs).
/// </summary>
internal sealed class OfficeEditorPane : DockPanel, IDisposable
{
    private static readonly double[] FontSizes = [8, 9, 10, 10.5, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 72];
    private static readonly (string Title, string Style)[] ParagraphStyles =
    [
        ("Обычный", "Standard"), ("Основной текст", "Text body"), ("Название", "Title"),
        ("Заголовок 1", "Heading 1"), ("Заголовок 2", "Heading 2"), ("Заголовок 3", "Heading 3"),
        ("Цитата", "Quotations")
    ];

    private readonly string _path;
    private readonly Action<string> _status;
    private readonly EmbeddedWindowHost _host = new();
    private readonly ContentControl _overlay = new() { Background = Brushes.White };
    private readonly WrapPanel _tools = new() { ItemSpacing = 4, LineSpacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<string, Button> _toggles = new(StringComparer.Ordinal);
    private readonly List<Control> _documentControls = [];
    private readonly List<Control> _editFeatureControls = [];
    private readonly Button _save;
    private readonly Button _undo;
    private readonly Button _redo;
    private readonly OfficeRecoveryStore _recovery = new();
    private readonly DispatcherTimer _recoveryTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly SemaphoreSlim _persistence = new(1, 1);
    private readonly Border _recoveryBanner = new() { IsVisible = false };
    private readonly Border _saveConflictBanner = new() { IsVisible = false };
    private bool _checkpointing;
    private ComboBox? _font;
    private ComboBox? _size;
    private ComboBox? _style;
    private EditorDocument? _document;
    private bool _started;
    private bool _syncing;
    private bool _disposed;

    public OfficeEditorPane(string path, Action<string> status)
    {
        _path = Path.GetFullPath(path);
        _status = status;
        _recoveryTimer.Tick += (_, _) => _ = CheckpointAsync();

        _save = TextButton("Сохранить", Icons.Save, "Сохранить документ (Ctrl+S)", () => _ = SaveAsync());
        _undo = IconButton(Icons.Undo, "Отменить (Ctrl+Z)", () => Dispatch(".uno:Undo"));
        _redo = IconButton(Icons.Redo, "Повторить (Ctrl+Y)", () => Dispatch(".uno:Redo"));
        Add(_save, _undo, _redo);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Top };
        right.Children.Add(Track(IconButton(Icons.Minus, "Уменьшить масштаб", () => Dispatch(".uno:ZoomMinus"))));
        right.Children.Add(Track(IconButton(Icons.ZoomIn, "Увеличить масштаб", () => Dispatch(".uno:ZoomPlus"))));
        right.Children.Add(Track(IconButton(Icons.Search, "Найти и заменить (Ctrl+H)", () => Dispatch(".uno:SearchDialog"))));
        right.Children.Add(Track(TextButton("PDF", Icons.FileText, "Сохранить как PDF", () => _ = ExportPdfAsync())));
        right.Children.Add(Track(TextButton("Копия…", Icons.Files, "Сохранить копию в другом формате или папке", () => _ = SaveCopyAsync())));
        right.Children.Add(Track(TextButton("Печать", Icons.Printer, "Печать через мастер ГЭТ (Ctrl+P)", () => PrintRequested?.Invoke())));

        var bar = new DockPanel { Margin = new Thickness(16, 8), LastChildFill = true };
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        bar.Children.Add(_tools);
        var header = new Border { Child = bar };
        header.Classes.Add("header");
        SetDock(header, Dock.Top);
        Children.Add(header);
        DockPanel.SetDock(_recoveryBanner, Dock.Top);
        Children.Add(_recoveryBanner);
        DockPanel.SetDock(_saveConflictBanner, Dock.Top);
        Children.Add(_saveConflictBanner);

        var body = new Grid();
        _host.IsVisible = false;
        body.Children.Add(_host);
        body.Children.Add(_overlay);
        Children.Add(body);
        SetEnabled(false);
        ShowMessage("Открываем документ…", "ГЭТ Документ готовит редактор. Обычно это занимает пару секунд, при первом запуске — до минуты.");
        AttachedToVisualTree += (_, _) =>
        {
            if (!_started)
            {
                _started = true;
                Dispatcher.UIThread.Post(() => _ = StartAsync(), DispatcherPriority.Background);
            }
            else if (_document is { } shown)
            {
                // Back on this tab: typing goes to the document, not to the tab strip.
                Dispatcher.UIThread.Post(() => _ = Run(shown.FocusAsync), DispatcherPriority.Background);
            }
        };
    }

    public string DocumentPath => _path;
    public bool HasUnsavedChanges => _document?.IsModified == true;

    /// <summary>The document's modified flag changed (tab caption marks unsaved changes).</summary>
    public event Action<bool>? ModifiedChanged;
    /// <summary>Print was asked for (button or Ctrl+P): MainWindow opens the GET print wizard.</summary>
    public event Action? PrintRequested;
    /// <summary>Close was asked for from inside the document (Ctrl+W / Ctrl+F4).</summary>
    public event Action? CloseRequested;
    /// <summary>Open a verified recovery copy in another GET Document tab, leaving the source untouched.</summary>
    public event Action<string>? RecoveryCopyRequested;

    private bool SupportsRecovery => Path.GetExtension(_path).ToLowerInvariant() is ".docx" or ".xlsx" or ".pptx";

    private async Task StartAsync()
    {
        try
        {
            var document = await LibreOfficeEditor.Shared.OpenAsync(_path);
            if (_disposed) { await document.CloseAsync(); return; }
            _document = document;
            document.StateChanged += (command, enabled, value) => Dispatcher.UIThread.Post(() => OnState(command, enabled, value));
            document.ModifiedChanged += modified => Dispatcher.UIThread.Post(() => OnModified(modified));
            document.CommandRequested += command => Dispatcher.UIThread.Post(() => OnCommand(command));
            document.Closed += () => Dispatcher.UIThread.Post(OnClosedByOffice);
            BuildTools(document.Kind);

            IntPtr window = IntPtr.Zero;
            for (int attempt = 0; attempt < 50 && window == IntPtr.Zero && !_disposed; attempt++)
            {
                window = EmbeddedWindowHost.FindWindow(document.Marker);
                if (window == IntPtr.Zero) await Task.Delay(100);
            }
            if (window == IntPtr.Zero) throw new InvalidOperationException("Окно документа LibreOffice не найдено.");
            // The native host paints over Avalonia content, so it only appears once there is a document in it.
            _host.IsVisible = true;
            for (int attempt = 0; attempt < 20 && !_host.IsReady; attempt++)
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            _host.Embed(window);
            await document.ShowAsync();
            _host.Fit();
            _overlay.IsVisible = false;
            SetEnabled(true);
            OnModified(false);
            _status(document.ReadOnly
                ? $"«{Path.GetFileName(_path)}» открыт только для чтения: файл занят или защищён. Изменения можно сохранить копией."
                : $"«{Path.GetFileName(_path)}» открыт для редактирования.");
            if (SupportsRecovery)
            {
                try
                {
                    OfficeRecoveryCopy? copy = await Task.Run(() => _recovery.FindLatest(_path));
                    if (!_disposed && copy is not null) ShowRecoveryBanner(copy);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _status("Невозможно проверить аварийные копии: " + ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or
                                   UnauthorizedAccessException or NotSupportedException or InvalidDataException)
        {
            if (_disposed) return;
            ShowMessage("Не удалось открыть документ", ex.Message);
            _status("Документ не открыт: " + ex.Message);
        }
    }

    // --- toolbar --------------------------------------------------------------------------------

    private void BuildTools(string kind)
    {
        Add(Separator());
        if (kind == "writer")
        {
            _style = Combo(ParagraphStyles.Select(s => s.Title), 140, "Стиль абзаца");
            _style.SelectionChanged += (_, _) =>
            {
                if (_syncing || _style.SelectedIndex < 0) return;
                Dispatch(".uno:StyleApply", new() { ["Style"] = ParagraphStyles[_style.SelectedIndex].Style, ["FamilyName"] = "ParagraphStyles" });
            };
            Add(_style);
        }
        _font = Combo(FontManager.Current.SystemFonts.Select(f => f.Name).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase), 170, "Шрифт");
        _font.SelectionChanged += (_, _) =>
        {
            if (_syncing || _font.SelectedItem is not string name) return;
            Dispatch(".uno:CharFontName", new() { ["CharFontName.FamilyName"] = name });
        };
        _size = Combo(FontSizes.Select(s => s.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)), 72, "Размер шрифта");
        _size.SelectionChanged += (_, _) =>
        {
            if (_syncing || _size.SelectedIndex < 0) return;
            Dispatch(".uno:FontHeight", new() { ["FontHeight.Height"] = FontSizes[_size.SelectedIndex] });
        };
        Add(_font, _size);
        Add(Toggle(".uno:Bold", "Ж", "Полужирный (Ctrl+B)", FontWeight.Bold, FontStyle.Normal, false),
            Toggle(".uno:Italic", "К", "Курсив (Ctrl+I)", FontWeight.Normal, FontStyle.Italic, false),
            Toggle(".uno:Underline", "Ч", "Подчёркнутый (Ctrl+U)", FontWeight.Normal, FontStyle.Normal, true));
        Add(Separator());

        if (kind == "calc")
        {
            Add(Toggle(".uno:AlignLeft", Icons.AlignLeft, "По левому краю"),
                Toggle(".uno:AlignHorizontalCenter", Icons.AlignCenter, "По центру"),
                Toggle(".uno:AlignRight", Icons.AlignRight, "По правому краю"),
                Toggle(".uno:WrapText", Icons.WrapText, "Переносить текст"),
                Toggle(".uno:ToggleMergeCells", Icons.Merge, "Объединить ячейки"));
            Add(Separator());
            Add(MenuButton("Формат", Icons.Sliders, "Формат чисел",
                ("Общий", ".uno:NumberFormatStandard"), ("Число 0,00", ".uno:NumberFormatDecimal"),
                ("Денежный", ".uno:NumberFormatCurrency"), ("Процент", ".uno:NumberFormatPercent"),
                ("Дата", ".uno:NumberFormatDate")));
            Add(IconButton(Icons.Sum, "Автосумма", () => Dispatch(".uno:AutoSum")),
                IconButton(Icons.RowInsert, "Вставить строку выше", () => Dispatch(".uno:InsertRowsBefore")),
                IconButton(Icons.ColumnInsert, "Вставить столбец слева", () => Dispatch(".uno:InsertColumnsBefore")),
                IconButton(Icons.Trash, "Удалить строки выделения", () => Dispatch(".uno:DeleteRows")),
                IconButton(Icons.SortAsc, "Сортировать по возрастанию", () => Dispatch(".uno:SortAscending")),
                Toggle(".uno:DataFilterAutoFilter", Icons.Filter, "Фильтр"));
            Add(Separator());
            Add(MenuButton("Закрепление", Icons.Table, "Закрепление строк и столбцов",
                    ("Верхняя строка", "feature:calc-freeze-top-row"),
                    ("Первый столбец", "feature:calc-freeze-first-column"),
                    ("Снять закрепление", "feature:calc-unfreeze")),
                EditFeature(TextButton("Пересчитать", Icons.Refresh, "Пересчитать все формулы",
                    () => RunFeature("calc-recalculate"))));
            Add(EditFeature(MenuButton("Печать таблицы", Icons.Printer, "Настройка области и строк, повторяемых при печати",
                ("Печатать заполненную область", "feature:calc-print-used-area"),
                ("Сбросить область печати", "feature:calc-clear-print-area"),
                ("Повторять первую строку", "feature:calc-repeat-first-row"),
                ("Отключить повтор строк", "feature:calc-stop-repeat-rows"))));
            Add(Separator());
            Add(EditFeature(MenuButton("Диаграмма", Icons.Presentation,
                    "Выделите диапазон с числовыми данными, затем выберите тип диаграммы",
                    ("Столбчатая", "feature:calc-chart-column"),
                    ("Линейная", "feature:calc-chart-line"))),
                EditFeature(MenuButton("Проверка данных", Icons.Check,
                    "Проверка данных для выделенных ячеек",
                    ("Только целые числа ≥ 0", "feature:calc-validate-whole-nonnegative"),
                    ("Только числа ≥ 0", "feature:calc-validate-decimal-nonnegative"),
                    ("Снять ограничения", "feature:calc-validate-clear"))));
            Add(Separator());
            Add(EditFeature(TextButton("Сводная", Icons.Table,
                    "Сводная на отдельном листе: выделите два столбца (категория и сумма), включая заголовки",
                    () => RunFeature("calc-pivot-sum"))),
                EditFeature(TextButton("Список…", Icons.ListBullets,
                    "Создать выпадающий список для выделенных ячеек",
                    () => _ = EditChoiceListAsync())));
            return;
        }

        Add(Toggle(".uno:LeftPara", Icons.AlignLeft, "По левому краю (Ctrl+L)"),
            Toggle(".uno:CenterPara", Icons.AlignCenter, "По центру (Ctrl+E)"),
            Toggle(".uno:RightPara", Icons.AlignRight, "По правому краю (Ctrl+R)"));
        if (kind == "writer") Add(Toggle(".uno:JustifyPara", Icons.AlignJustify, "По ширине (Ctrl+J)"));
        Add(Toggle(".uno:DefaultBullet", Icons.ListBullets, "Маркированный список"),
            Toggle(".uno:DefaultNumbering", Icons.ListNumbers, "Нумерованный список"));
        if (kind == "writer")
        {
            Add(IconButton(Icons.IndentLess, "Уменьшить отступ", () => Dispatch(".uno:DecrementIndent")),
                IconButton(Icons.IndentMore, "Увеличить отступ", () => Dispatch(".uno:IncrementIndent")));
            Add(Separator());
            Add(MenuButton("Таблица", Icons.Table, "Вставить таблицу",
                    ("2 × 2", "table:2:2"), ("3 × 3", "table:3:3"), ("4 × 4", "table:4:4"), ("5 × 5", "table:5:5"),
                    ("Другой размер…", ".uno:InsertTable")),
                IconButton(Icons.Image, "Вставить изображение", () => _ = InsertImageAsync()),
                IconButton(Icons.PageBreak, "Разрыв страницы (Ctrl+Enter)", () => Dispatch(".uno:InsertPagebreak")));
            Add(EditFeature(MenuButton("Оглавление", Icons.ListNumbers, "Оглавление по стилям заголовков",
                ("Вставить оглавление", "feature:writer-toc-insert"),
                ("Обновить оглавления", "feature:writer-toc-update"))));
            return;
        }
        Add(Separator());
        Add(TextButton("Слайд", Icons.SlideAdd, "Новый слайд", () => Dispatch(".uno:InsertPage")),
            EditFeature(TextButton("Копия слайда", Icons.Files, "Дублировать текущий слайд",
                () => RunFeature("impress-duplicate-slide"))),
            IconButton(Icons.Trash, "Удалить слайд", () => Dispatch(".uno:DeletePage")),
            IconButton(Icons.Image, "Вставить изображение", () => _ = InsertImageAsync()),
            TextButton("Показ", Icons.Play, "Показ слайдов (F5)", () => Dispatch(".uno:Presentation")));
    }

    private void Add(params Control[] controls)
    {
        foreach (var control in controls)
        {
            _tools.Children.Add(control);
            if (control is not Border) _documentControls.Add(control);
        }
    }

    private T EditFeature<T>(T control) where T : Control
    {
        _editFeatureControls.Add(control);
        return control;
    }

    private T Track<T>(T control) where T : Control
    {
        _documentControls.Add(control);
        return control;
    }

    private static Border Separator() => new()
    {
        Width = 1, Height = 24, Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center,
        Background = Application.Current?.FindResource("GetBorderBrush") as IBrush ?? Brushes.LightGray
    };

    private static Button IconButton(Geometry icon, string tip, Action action)
    {
        var button = new Button { Content = Icons.Create(icon, 18), Width = 36, MinHeight = 36, Padding = new Thickness(0) };
        button.Classes.Add("ghost");
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    private static Button TextButton(string text, Geometry icon, string tip, Action action)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(Icons.Create(icon, 18));
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, MinHeight = 36 };
        button.Classes.Add("ghost");
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, text);
        button.Click += (_, _) => action();
        return button;
    }

    private Button Toggle(string command, Geometry icon, string tip)
    {
        var button = IconButton(icon, tip, () => Dispatch(command));
        _toggles[command] = button;
        return button;
    }

    private Button Toggle(string command, string letter, string tip, FontWeight weight, FontStyle style, bool underline)
    {
        var text = new TextBlock
        {
            Text = letter, FontSize = 16, FontWeight = weight, FontStyle = style,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            TextDecorations = underline ? TextDecorations.Underline : null
        };
        var button = new Button { Content = text, Width = 36, MinHeight = 36, Padding = new Thickness(0) };
        button.Classes.Add("ghost");
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => Dispatch(command);
        _toggles[command] = button;
        return button;
    }

    private Button MenuButton(string text, Geometry icon, string tip, params (string Title, string Command)[] items)
    {
        var flyout = new MenuFlyout();
        foreach (var (title, command) in items)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => RunMenuCommand(command);
            flyout.Items.Add(item);
        }
        var button = TextButton(text, icon, tip, () => { });
        button.Flyout = flyout;
        return button;
    }

    private void RunMenuCommand(string command)
    {
        if (command.StartsWith("feature:", StringComparison.Ordinal))
        {
            RunFeature(command["feature:".Length..]);
            return;
        }
        if (command.StartsWith("table:", StringComparison.Ordinal))
        {
            var size = command.Split(':');
            Dispatch(".uno:InsertTable", new() { ["Columns"] = int.Parse(size[1]), ["Rows"] = int.Parse(size[2]) });
            return;
        }
        Dispatch(command);
    }

    private static ComboBox Combo(IEnumerable<string> items, double width, string name)
    {
        var combo = new ComboBox { Width = width, MinHeight = 36, ItemsSource = items.ToList(), MaxDropDownHeight = 420 };
        ToolTip.SetTip(combo, name);
        Avalonia.Automation.AutomationProperties.SetName(combo, name);
        return combo;
    }

    private void SetEnabled(bool enabled)
    {
        foreach (var control in _documentControls.Append(_save).Append(_undo).Append(_redo)) control.IsEnabled = enabled;
        foreach (var control in _editFeatureControls)
            control.IsEnabled = enabled && _document?.ReadOnly != true;
    }

    // --- LibreOffice state ------------------------------------------------------------------------

    private void OnState(string command, bool enabled, JsonElement value)
    {
        if (_disposed) return;
        if (command == ".uno:Undo") { _undo.IsEnabled = enabled; return; }
        if (command == ".uno:Redo") { _redo.IsEnabled = enabled; return; }
        _syncing = true;
        try
        {
            if (_toggles.TryGetValue(command, out var toggle))
            {
                bool on = value.ValueKind == JsonValueKind.True ||
                          (value.ValueKind == JsonValueKind.Number && value.GetDouble() != 0);
                toggle.Classes.Set("active", on);
                toggle.IsEnabled = enabled;
            }
            else if (command == ".uno:CharFontName" && _font is not null && value.ValueKind == JsonValueKind.String)
            {
                string name = value.GetString() ?? "";
                _font.SelectedItem = (_font.ItemsSource as IList<string>)?.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
                _font.PlaceholderText = name;
            }
            else if (command == ".uno:FontHeight" && _size is not null && value.ValueKind == JsonValueKind.Number)
            {
                double height = Math.Round(value.GetDouble() * 2) / 2;
                _size.SelectedIndex = Array.IndexOf(FontSizes, height);
                _size.PlaceholderText = height.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
            }
            else if (command == ".uno:StyleApply" && _style is not null && value.ValueKind == JsonValueKind.String)
            {
                string style = value.GetString() ?? "";
                int index = Array.FindIndex(ParagraphStyles, s =>
                    s.Title.Equals(style, StringComparison.OrdinalIgnoreCase) || s.Style.Equals(style, StringComparison.OrdinalIgnoreCase));
                _style.SelectedIndex = index;
                _style.PlaceholderText = style;
            }
        }
        finally { _syncing = false; }
    }

    private void OnModified(bool modified)
    {
        if (_disposed) return;
        _save.Classes.Set("primary", modified);
        _save.Classes.Set("ghost", !modified);
        if (modified && SupportsRecovery) _recoveryTimer.Start();
        else _recoveryTimer.Stop();
        ModifiedChanged?.Invoke(modified);
    }

    private void OnCommand(string command)
    {
        switch (command)
        {
            case ".uno:Save":
            case ".uno:SaveAll":
                _ = SaveAsync();
                break;
            case ".uno:SaveAs":
                _ = SaveCopyAsync();
                break;
            case ".uno:Print":
            case ".uno:PrintDefault":
            case ".uno:PrintPreview":
                PrintRequested?.Invoke();
                break;
            case ".uno:ExportToPDF":
            case ".uno:ExportDirectToPDF":
                _ = ExportPdfAsync();
                break;
            case ".uno:CloseDoc":
            case ".uno:CloseWin":
                CloseRequested?.Invoke();
                break;
            default:
                _status("Эта команда в ГЭТ Документ выполняется кнопками на панели и стартовом экране.");
                break;
        }
    }

    private void OnClosedByOffice()
    {
        if (_disposed || _document is null) return;
        _recoveryTimer.Stop();
        _document = null;
        SetEnabled(false);
        ShowMessage("Редактор закрылся", "LibreOffice неожиданно завершил работу. Закройте вкладку и откройте документ снова — " +
            "последняя сохранённая версия файла не пострадала.");
        _status("Редактор документа закрылся.");
    }

    // --- commands ---------------------------------------------------------------------------------

    private void RunFeature(string featureId)
    {
        if (_document is not { } document) return;
        OfficeFeatureDefinition feature;
        try { feature = OfficeFeatureCatalog.Require(document.Kind, featureId, document.ReadOnly,
                OfficeFeatureProvider.LibreOfficeUno); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            _status(ex.Message);
            return;
        }
        _ = Run(async () =>
        {
            await document.ExecuteFeatureAsync(featureId);
            _status("Готово: " + feature.Title);
        });
    }

    private async Task EditChoiceListAsync()
    {
        if (_document is not { Kind: "calc", ReadOnly: false } document) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 155,
            MaxLength = 1000,
            PlaceholderText = "Например: В работе / Завершено / На проверке",
            Text = "В работе\nЗавершено\nНа проверке"
        };
        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Firebrick
        };
        var confirm = new Button { Content = "Применить", MinHeight = 38, MinWidth = 110 };
        confirm.Classes.Add("primary");
        var cancel = new Button { Content = "Отмена", MinHeight = 38, MinWidth = 90 };
        cancel.Classes.Add("ghost");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(confirm);
        actions.Children.Add(cancel);
        var layout = new StackPanel { Margin = new Thickness(22), Spacing = 12 };
        layout.Children.Add(new TextBlock { Text = "Список значений", Classes = { "title" } });
        layout.Children.Add(new TextBlock
        {
            Text = "Каждый вариант — с новой строки. От 2 до 20 значений, максимум 40 символов в пункте. " +
                   "Список будет применён к выделенным ячейкам вместо их прежнего правила проверки.",
            TextWrapping = TextWrapping.Wrap
        });
        layout.Children.Add(input);
        layout.Children.Add(error);
        layout.Children.Add(actions);
        var dialog = new Window
        {
            Title = "ГЭТ Документ — выпадающий список",
            Width = 530,
            Height = 390,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White,
            Content = layout
        };
        confirm.Click += (_, _) =>
        {
            try
            {
                string[] items = OfficeChoiceList.ParseLines(input.Text ?? "");
                dialog.Close(items);
            }
            catch (ArgumentException ex) { error.Text = ex.Message; }
        };
        cancel.Click += (_, _) => dialog.Close(null);
        string[]? choices = await dialog.ShowDialog<string[]?>(owner);
        if (choices is null || _disposed || _document != document) return;

        try
        {
            await document.ExecuteFeatureAsync("calc-validate-choice-list", choices);
            _status("Выпадающий список добавлен к выделению. Исходные значения ячеек не изменены.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or
                                   UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _status("Не удалось создать выпадающий список: " + ex.Message);
        }
    }

    private void Dispatch(string command, Dictionary<string, object>? arguments = null)
    {
        if (_document is not { } document) return;
        _ = Run(() => document.DispatchAsync(command, arguments));
    }

    private async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or
                                   UnauthorizedAccessException or NotSupportedException or InvalidDataException)
        {
            _status(ex.Message);
        }
    }

    public async Task SaveAsync()
    {
        if (_document is not { } document) return;
        if (document.ReadOnly)
        {
            _status("Файл открыт только для чтения. Сохраните изменения копией.");
            await SaveCopyAsync();
            return;
        }
        await _persistence.WaitAsync();
        try
        {
            if (_disposed) return;
            await document.SaveAsync();
            // Deleting old recovery data is allowed only after the original save succeeded.
            if (SupportsRecovery)
            {
                try { _recovery.Discard(_path); _recoveryBanner.IsVisible = false; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _status("Сохранено, но не удалось удалить старые аварийные копии: " + ex.Message);
                    return;
                }
            }
            _status($"Сохранено: {Path.GetFileName(_path)} · {DateTime.Now:HH:mm}");
        }
        catch (OfficeFileConflictException ex)
        {
            ShowSaveConflictBanner();
            _status("Сохранение отменено: " + ex.Message);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or
                                   TimeoutException or InvalidDataException or UnauthorizedAccessException)
        {
            _status("Не удалось сохранить: " + ex.Message);
        }
        finally { _persistence.Release(); }
    }

    private void ShowSaveConflictBanner()
    {
        if (_disposed) return;
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(16, 6)
        };
        bar.Children.Add(new TextBlock
        {
            Text = "Файл изменён на диске или в сетевой папке. Исходник не перезаписан; " +
                   "сохраните свою редакцию отдельной копией.",
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 620
        });
        var copy = new Button { Content = "Сохранить копию…", MinHeight = 32 };
        copy.Classes.Add("primary");
        copy.Click += (_, _) => _ = SaveCopyAsync();
        bar.Children.Add(copy);
        var hide = new Button { Content = "Скрыть", MinHeight = 32 };
        hide.Classes.Add("ghost");
        hide.Click += (_, _) => _saveConflictBanner.IsVisible = false;
        bar.Children.Add(hide);
        _saveConflictBanner.Child = bar;
        _saveConflictBanner.Classes.Add("header");
        _saveConflictBanner.IsVisible = true;
    }

    private async Task CheckpointAsync()
    {
        if (_checkpointing || _disposed || _document is not { IsModified: true, ReadOnly: false } document || !SupportsRecovery)
            return;
        _checkpointing = true;
        try
        {
            await _persistence.WaitAsync();
            try
            {
                if (_disposed || !document.IsModified) return;
                await Task.Run(() => _recovery.CaptureAsync(_path, document.ExportRecoveryCopyAsync));
            }
            finally { _persistence.Release(); }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or
                                   UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            if (!_disposed) _status("Не удалось создать аварийную копию: " + ex.Message);
        }
        finally { _checkpointing = false; }
    }

    private void ShowRecoveryBanner(OfficeRecoveryCopy copy)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(16, 6) };
        bar.Children.Add(new TextBlock
        {
            Text = $"Найдена аварийная копия от {copy.CapturedUtc.ToLocalTime():dd.MM HH:mm}. Исходный файл не изменён.",
            VerticalAlignment = VerticalAlignment.Center
        });
        var open = new Button { Content = "Открыть копию", MinHeight = 32 };
        open.Classes.Add("primary");
        open.Click += (_, _) =>
        {
            OfficeRecoveryCopy? verified = _recovery.FindLatest(_path);
            if (verified is null)
            {
                _status("Аварийная копия больше недоступна или повреждена.");
                _recoveryBanner.IsVisible = false;
                return;
            }
            RecoveryCopyRequested?.Invoke(verified.FilePath);
        };
        bar.Children.Add(open);
        var dismiss = new Button { Content = "Скрыть", MinHeight = 32 };
        dismiss.Classes.Add("ghost");
        dismiss.Click += (_, _) => _recoveryBanner.IsVisible = false;
        bar.Children.Add(dismiss);
        _recoveryBanner.Child = bar;
        _recoveryBanner.Classes.Add("header");
        _recoveryBanner.IsVisible = true;
    }

    private async Task SaveCopyAsync()
    {
        if (_document is not { } document || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        string extension = Path.GetExtension(_path).TrimStart('.').ToLowerInvariant();
        string[] formats = document.Kind switch
        {
            "calc" => ["xlsx", "ods", "xls", "csv"],
            "impress" => ["pptx", "odp", "ppt"],
            _ => ["docx", "odt", "doc", "rtf"]
        };
        var ordered = formats.OrderBy(f => f == extension ? 0 : 1).ToArray();
        var chosen = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить новую копию (без перезаписи)",
            SuggestedFileName = Path.GetFileNameWithoutExtension(_path) + " (копия)." + ordered[0],
            DefaultExtension = ordered[0],
            ShowOverwritePrompt = false,
            FileTypeChoices = ordered.Select(f => new FilePickerFileType(f.ToUpperInvariant()) { Patterns = ["*." + f] }).ToList()
        });
        if (chosen is null || !chosen.Path.IsFile) return;
        string target = chosen.Path.LocalPath;
        if (string.Equals(Path.GetFullPath(target), _path, StringComparison.OrdinalIgnoreCase))
        {
            await SaveAsync();
            return;
        }
        await Run(async () =>
        {
            await document.SaveAsAsync(target);
            _status("Копия сохранена: " + Path.GetFileName(target));
        });
    }

    private async Task ExportPdfAsync()
    {
        if (_document is not { } document || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var chosen = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить как PDF",
            SuggestedFileName = Path.GetFileNameWithoutExtension(_path) + ".pdf",
            DefaultExtension = "pdf",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }]
        });
        if (chosen is null || !chosen.Path.IsFile) return;
        await Run(async () =>
        {
            await document.ExportPdfAsync(chosen.Path.LocalPath);
            _status("PDF сохранён: " + Path.GetFileName(chosen.Path.LocalPath));
        });
    }

    /// <summary>PDF of the document as it is on screen (unsaved edits included), for the print wizard.</summary>
    public async Task<string?> PreparePrintableAsync()
    {
        if (_document is not { } document) return null;
        string folder = Path.Combine(Path.GetTempPath(), "GET-Document-print");
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, Path.GetFileNameWithoutExtension(_path) + "-" + Guid.NewGuid().ToString("N")[..8] + ".pdf");
        try
        {
            await document.ExportPdfAsync(target);
            return File.Exists(target) ? target : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            _status("Не удалось подготовить печать: " + ex.Message);
            return null;
        }
    }

    private async Task InsertImageAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Вставить изображение",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Изображения") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.tif", "*.tiff", "*.svg"] }]
        });
        if (files.Count == 0 || !files[0].Path.IsFile) return;
        Dispatch(".uno:InsertGraphic", new() { ["FileName"] = new Uri(files[0].Path.LocalPath).AbsoluteUri, ["AsLink"] = false });
    }

    private void ShowMessage(string title, string text)
    {
        var panel = new StackPanel { Margin = new Thickness(40, 32), Spacing = 10, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        var heading = new TextBlock { Text = title };
        heading.Classes.Add("title");
        var body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        body.Classes.Add("hint");
        panel.Children.Add(heading);
        panel.Children.Add(body);
        _overlay.Content = panel;
        _overlay.IsVisible = true;
        _host.IsVisible = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recoveryTimer.Stop();
        var document = _document;
        _document = null;
        if (document is not null) _ = document.CloseAsync();
        _host.Release();
    }
}
