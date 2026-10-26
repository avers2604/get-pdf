using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using GetDocument.Core.Preview;
using GetDocument.Desktop.Preview;
using GetPdf;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace GetDocument.Desktop.Pdf;

internal sealed partial class PdfWorkspacePane
{
    private static readonly string[] StampPresets =
        ["КОПИЯ", "КОПИЯ ВЕРНА", "УТВЕРЖДАЮ", "СОГЛАСОВАНО", "ДЛЯ СЛУЖЕБНОГО ПОЛЬЗОВАНИЯ", "ОБРАЗЕЦ", "ПОГАШЕНО"];

    private readonly List<Control> _modifyingControls = [];
    private readonly List<Control> _markupControls = [];
    private ComboBox? _stampBox;
    private Button? _rectMode, _noteMode;

    // ------------------------------------------------------------------ editor panel

    private Control BuildTools()
    {
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Страницы", Content = Scroll(PageTools()) });
        tabs.Items.Add(new TabItem { Header = "Разметка", Content = Scroll(MarkupTools()) });
        tabs.Items.Add(new TabItem { Header = "Документ", Content = Scroll(DocumentTools()) });
        return tabs;
    }

    private static ScrollViewer Scroll(Control content) => new() { Content = content, Padding = new Thickness(16, 8, 16, 16) };

    private StackPanel PageTools()
    {
        var panel = new StackPanel { Spacing = 6 };
        Section(panel, "Текущая страница", "Действия относятся к странице, выбранной в списке или видимой сейчас.");
        var rotate = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        rotate.Children.Add(Modifying(ToolButton("Повернуть влево", Icons.RotateLeft, () =>
            Change(p => p.RotateCounterclockwise(_current), "Страница повёрнута влево."))));
        rotate.Children.Add(Modifying(ToolButton("Вправо", Icons.Refresh, () =>
            Change(p => p.RotateClockwise(_current), "Страница повёрнута вправо."))));
        panel.Children.Add(rotate);
        var move = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        move.Children.Add(Modifying(ToolButton("Выше", Icons.ChevronUp, () => MovePage(-1))));
        move.Children.Add(Modifying(ToolButton("Ниже", Icons.ChevronDown, () => MovePage(1))));
        panel.Children.Add(move);
        panel.Children.Add(Modifying(ToolButton("Дублировать страницу", Icons.Files, () =>
            Change(p => p.Duplicate(_current), "Страница продублирована."))));
        panel.Children.Add(Modifying(ToolButton("Удалить страницу", Icons.Trash, DeletePage, danger: true)));
        Section(panel, "Документ из страниц", "Новый PDF создаётся рядом; этот файл не меняется.");
        panel.Children.Add(Modifying(ToolButton("Извлечь страницы в PDF…", Icons.FileText, () => _ = ExtractAsync())));
        panel.Children.Add(Modifying(ToolButton("Вставить страницы из PDF…", Icons.Plus, () => _ = InsertFromPdfAsync())));
        panel.Children.Add(Modifying(ToolButton("Объединить с другими PDF…", Icons.Files, () => _ = MergeAsync())));
        return panel;
    }

    private StackPanel MarkupTools()
    {
        var panel = new StackPanel { Spacing = 6 };
        Section(panel, "Штамп", "Видимая надпись в левом верхнем углу страницы. Это не подпись и не печать.");
        _stampBox = new ComboBox { ItemsSource = StampPresets, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(Modifying(_stampBox));
        var stampRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        stampRow.Children.Add(Modifying(ToolButton("На страницу", Icons.Check, () => SetStamp(all: false), primary: true)));
        stampRow.Children.Add(Modifying(ToolButton("На все", Icons.Files, () => SetStamp(all: true))));
        panel.Children.Add(stampRow);
        panel.Children.Add(Modifying(ToolButton("Убрать штамп", Icons.X, () =>
            Change(p => p.SetStamp(_current, ""), "Штамп убран."))));
        Section(panel, "Нумерация страниц", "«1», «1 из N» или «Страница 1 из N»; титульный лист можно оставить без номера.");
        panel.Children.Add(Modifying(ToolButton("Пронумеровать страницы…", Icons.ListNumbers, () => _ = NumberPagesAsync())));
        Section(panel, "Рамки и заметки", "Включите режим и щёлкните (для рамки — потяните) по странице. Esc — выйти из режима.");
        _rectMode = Markup(ToolButton("Рисовать рамку", Icons.Square, () => SetMode(_mode == Mode.Rectangle ? Mode.Browse : Mode.Rectangle)));
        _noteMode = Markup(ToolButton("Ставить заметку", Icons.Note, () => SetMode(_mode == Mode.Note ? Mode.Browse : Mode.Note)));
        panel.Children.Add(_rectMode);
        panel.Children.Add(_noteMode);
        panel.Children.Add(Markup(ToolButton("Очистить разметку страницы", Icons.X, () =>
            Change(p => { p.ClearRectangles(_current); p.ClearNotes(_current); }, "Рамки и заметки страницы удалены."))));
        return panel;
    }

    private StackPanel DocumentTools()
    {
        var panel = new StackPanel { Spacing = 6 };
        Section(panel, "Сохранение", "Все изменения записываются в новый PDF. Исходный файл не перезаписывается.");
        panel.Children.Add(ToolButton("Сохранить как…", Icons.Save, () => _ = SaveAsAsync(), primary: true));
        Section(panel, "Сжатие", "Без потери качества (qpdf): текст, закладки и изображения не меняются.");
        var compress = ToolButton("Сжать PDF…", Icons.Minimize, () => _ = CompressAsync());
        compress.IsEnabled = PdfQpdfValidator.IsUsable;
        if (!compress.IsEnabled) ToolTip.SetTip(compress, "Компонент qpdf не установлен. Переустановите ГЭТ Документ.");
        panel.Children.Add(compress);
        Section(panel, "Проверка и формы", "");
        panel.Children.Add(ToolButton("Проверить подписи…", Icons.ShieldCheck, () => _ = CheckSignaturesAsync()));
        panel.Children.Add(ToolButton("Заполнить поля формы…", Icons.Edit, () => _ = FillFormAsync()));
        panel.Children.Add(ToolButton("Сравнить с другим PDF…", Icons.Columns, () => _ = CompareAsync()));
        return panel;
    }

    private static void Section(StackPanel panel, string title, string hint)
    {
        var heading = new TextBlock { Text = title, Margin = new Thickness(0, 12, 0, 0) };
        heading.Classes.Add("section");
        heading.FontSize = 14;
        panel.Children.Add(heading);
        if (hint.Length == 0) return;
        var text = new TextBlock { Text = hint, FontSize = 12 };
        text.Classes.Add("hint");
        panel.Children.Add(text);
    }

    private static Button ToolButton(string text, Geometry icon, Action action, bool primary = false, bool danger = false)
    {
        var button = new Button
        {
            Content = MenuCaption(text, icon), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, MinHeight = 40
        };
        if (primary) button.Classes.Add("primary");
        if (danger) button.Classes.Add("danger");
        Avalonia.Automation.AutomationProperties.SetName(button, text);
        button.Click += (_, _) => action();
        return button;
    }

    private T Modifying<T>(T control) where T : Control { _modifyingControls.Add(control); return control; }
    private T Markup<T>(T control) where T : Control { _markupControls.Add(control); return control; }

    private void UpdateToolState()
    {
        foreach (var control in _modifyingControls) control.IsEnabled = _canModify;
        bool canMark = _plan.Pages.Count > 0 && CanMarkCurrent(out _);
        foreach (var control in _markupControls) control.IsEnabled = canMark;
        _rectMode?.Classes.Set("active", _mode == Mode.Rectangle);
        _noteMode?.Classes.Set("active", _mode == Mode.Note);
    }

    private void ToggleEditor()
    {
        _toolPane.IsVisible = !_toolPane.IsVisible;
        if (!_toolPane.IsVisible && _mode is Mode.Rectangle or Mode.Note) SetMode(Mode.Browse);
        if (_fitWidth) Relayout();
        UpdateState();
    }

    private void ToggleThumbs()
    {
        _thumbPane.IsVisible = !_thumbPane.IsVisible;
        if (_fitWidth) Relayout();
    }

    private void ToggleSelection() => SetMode(_mode == Mode.SelectText ? Mode.Browse : Mode.SelectText);

    private void SetMode(Mode mode)
    {
        if (mode is Mode.Rectangle or Mode.Note && !CanMarkCurrent(out string reason))
        {
            _status(reason);
            return;
        }
        _mode = mode;
        if (mode != Mode.SelectText) ClearSelection();
        Cursor = mode switch
        {
            Mode.SelectText => new Cursor(StandardCursorType.Ibeam),
            Mode.Rectangle or Mode.Note => new Cursor(StandardCursorType.Cross),
            _ => Cursor.Default
        };
        _status(mode switch
        {
            Mode.SelectText => "Выделите текст мышью; Ctrl+C или правая кнопка — копировать.",
            Mode.Rectangle => "Потяните по странице, чтобы нарисовать рамку. Esc — выйти из режима.",
            Mode.Note => "Щёлкните по странице, где поставить заметку. Esc — выйти из режима.",
            _ => "Режим просмотра."
        });
        UpdateState();
    }

    private ContextMenu PageContextMenu()
    {
        var menu = new ContextMenu();
        void Add(string text, Action action, bool modifying = true)
        {
            var item = new MenuItem { Header = text };
            item.Click += (_, _) => action();
            if (modifying) item.IsEnabled = _canModify;
            menu.Items.Add(item);
        }
        Add("Копировать выделенный текст", () => _ = CopySelectionAsync(), modifying: false);
        menu.Items.Add(new Separator());
        Add("Повернуть влево", () => Change(p => p.RotateCounterclockwise(_current), "Страница повёрнута влево."));
        Add("Повернуть вправо", () => Change(p => p.RotateClockwise(_current), "Страница повёрнута вправо."));
        Add("Переместить выше", () => MovePage(-1));
        Add("Переместить ниже", () => MovePage(1));
        Add("Дублировать", () => Change(p => p.Duplicate(_current), "Страница продублирована."));
        Add("Удалить страницу", DeletePage);
        return menu;
    }

    private void MovePage(int delta)
    {
        int to = _current + delta;
        if (to < 0 || to >= _plan.Pages.Count) return;
        int from = _current;
        Change(p => p.Move(from, to), delta < 0 ? "Страница перемещена выше." : "Страница перемещена ниже.");
        Dispatcher.Post(() => GoTo(to));
    }

    private static Avalonia.Threading.Dispatcher Dispatcher => Avalonia.Threading.Dispatcher.UIThread;

    private void DeletePage()
    {
        if (_plan.Pages.Count <= 1)
        {
            _status("Нельзя удалить единственную страницу документа.");
            return;
        }
        Change(p => p.Delete(_current), $"Страница {_current + 1} удалена. Ctrl+Z — вернуть.");
    }

    private void SetStamp(bool all)
    {
        string text = (_stampBox?.SelectedItem as string ?? "").Trim();
        if (text.Length == 0) return;
        if (all) Change(p => { for (int i = 0; i < p.Pages.Count; i++) p.SetStamp(i, text); }, $"Штамп «{text}» поставлен на все страницы.");
        else Change(p => p.SetStamp(_current, text), $"Штамп «{text}» поставлен на страницу {_current + 1}.");
    }

    // ------------------------------------------------------------------ pointer: selection, rectangles, notes

    private static Point Normalised(PdfPageView view, PointerEventArgs e)
    {
        var p = e.GetPosition(view);
        return new Point(Math.Clamp(p.X / Math.Max(1, view.Bounds.Width), 0, 1),
                         Math.Clamp(p.Y / Math.Max(1, view.Bounds.Height), 0, 1));
    }

    private async void OnPagePressed(PdfPageView view, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(view);
        if (!point.Properties.IsLeftButtonPressed) return;
        SetCurrent(view.Index, scroll: false);
        var at = Normalised(view, e);
        switch (_mode)
        {
            case Mode.SelectText:
                e.Pointer.Capture(view);
                var words = await WordsAsync(view.Reference);
                if (words.Words.Count == 0)
                {
                    _status("На этой странице нет текстового слоя (скан или изображение). Для сканов нужен OCR.");
                    return;
                }
                ClearSelection();
                _selectionPage = view.Index;
                _anchor = _end = words.HitTest(at.X, at.Y);
                ShowSelection();
                break;
            case Mode.Rectangle:
                e.Pointer.Capture(view);
                _dragStart = at;
                break;
            case Mode.Note:
                string? text = Owner is null ? null : await GetDialogs.AskTextAsync(Owner, "Заметка на странице",
                    "Текст заметки (до 120 символов). Она появится на странице жёлтым блоком.", "", 120, "Поставить");
                if (text is null) return;
                var note = new PdfTextNote(Math.Min(at.X, .6), Math.Min(at.Y, .88), text);
                Change(p => p.AddNote(view.Index, note), "Заметка добавлена.");
                break;
        }
    }

    private void OnPageMoved(PdfPageView view, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(view).Properties.IsLeftButtonPressed) return;
        var at = Normalised(view, e);
        if (_mode == Mode.SelectText && _selectionPage == view.Index &&
            _words.TryGetValue((view.Reference.SourcePage, view.Reference.Rotation), out var words))
        {
            int hit = words.HitTest(at.X, at.Y);
            if (hit >= 0 && hit != _end) { _end = hit; ShowSelection(); }
        }
        else if (_mode == Mode.Rectangle && _dragStart is { } start)
            view.SetDraft(Band(start, at));
    }

    private void OnPageReleased(PdfPageView view, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (_mode != Mode.Rectangle || _dragStart is not { } start) return;
        _dragStart = null;
        view.SetDraft(null);
        var band = Band(start, Normalised(view, e));
        if (band.Width < .01 || band.Height < .01) return;
        var rectangle = new PdfRectangleMarkup(band.X, band.Y, band.Width, band.Height);
        Change(p => p.AddRectangle(view.Index, rectangle), "Рамка добавлена.");
    }

    private static Rect Band(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private async Task<PdfWordSelection> WordsAsync(PdfPageReference page)
    {
        var key = (page.SourcePage, page.Rotation);
        if (_words.TryGetValue(key, out var cached)) return cached;
        PdfWordSelection words;
        try { words = await Task.Run(() => PdfTextSelectionReader.Read(_working, page.SourcePage, page.Rotation, _lifetime.Token)); }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException) { words = new PdfWordSelection([]); }
        _words[key] = words;
        return words;
    }

    private void ShowSelection()
    {
        foreach (var view in _pageViews) if (view.Index != _selectionPage) view.SetHighlights([], Colors.Transparent);
        if (_selectionPage < 0 || _anchor < 0 || _end < 0) return;
        var view0 = _pageViews[_selectionPage];
        if (!_words.TryGetValue((view0.Reference.SourcePage, view0.Reference.Rotation), out var words)) return;
        int a = Math.Min(_anchor, _end), b = Math.Max(_anchor, _end);
        view0.SetHighlights(words.Words.Skip(a).Take(b - a + 1).Select(w => w.Bounds).ToList(),
            Color.FromArgb(90, 0, 156, 188));
    }

    private void ClearSelection()
    {
        if (_selectionPage >= 0 && _selectionPage < _pageViews.Count) _pageViews[_selectionPage].SetHighlights([], Colors.Transparent);
        _selectionPage = _anchor = _end = -1;
        ApplySearchHighlight();
    }

    private async Task CopySelectionAsync()
    {
        if (_selectionPage < 0 || _anchor < 0) { _status("Сначала выделите текст в режиме «Выделение текста»."); return; }
        var view = _pageViews[_selectionPage];
        if (!_words.TryGetValue((view.Reference.SourcePage, view.Reference.Rotation), out var words)) return;
        string text = words.Extract(_anchor, _end);
        if (text.Length == 0) { _status("Выделение слишком большое для копирования."); return; }
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
            _status($"Скопировано символов: {text.Length}.");
        }
    }

    // ------------------------------------------------------------------ search

    private async Task SearchAsync()
    {
        string query;
        try { query = PdfTextSearchPlan.NormalizeQuery(_search.Text); }
        catch (ArgumentException) { _status("Введите от 1 до 128 символов без переноса строки."); return; }
        _status("Ищем…");
        try
        {
            var result = await Task.Run(() => PdfTextSearchService.Search(_working, query, _lifetime.Token), _lifetime.Token);
            _query = query;
            // Map source pages to the current (edited) order.
            var visible = new List<PdfTextPageMatch>();
            for (int i = 0; i < _plan.Pages.Count; i++)
            {
                var hit = result.Pages.FirstOrDefault(m => m.PageIndex == _plan.Pages[i].SourcePage);
                if (hit is not null) visible.Add(hit with { PageIndex = i });
            }
            _matches = visible;
            _matchIndex = -1;
            if (_matches.Count == 0)
            {
                _status(result.PagesWithoutTextLayer == result.InspectedPages
                    ? "В документе нет текстового слоя (скан): поиск невозможен без OCR."
                    : $"«{query}» не найдено.");
                ApplySearchHighlight();
                return;
            }
            FindNext(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            _status("Не удалось прочитать текст PDF: " + ex.Message);
        }
    }

    public void FindNext(bool reverse)
    {
        // A new query typed in the box: the arrow buttons / F3 start the search themselves.
        if (!string.IsNullOrWhiteSpace(_search.Text) && _search.Text.Trim() != _query) { _ = SearchAsync(); return; }
        if (_matches.Count == 0) { FocusSearch(); return; }
        _matchIndex = (_matchIndex + (reverse ? _matches.Count - 1 : 1) + _matches.Count) % _matches.Count;
        var hit = _matches[_matchIndex];
        GoTo(hit.PageIndex);
        _status($"«{_query}»: страница {hit.PageIndex + 1} — совпадение {_matchIndex + 1} из {_matches.Count} страниц. F3 — дальше.");
        ApplySearchHighlight();
    }

    private async void ApplySearchHighlight()
    {
        foreach (var other in _pageViews) if (other.Index != _selectionPage) other.SetHighlights([], Colors.Transparent);
        if (_matchIndex < 0 || _matchIndex >= _matches.Count || _query.Length == 0) return;
        int index = _matches[_matchIndex].PageIndex;
        if (index >= _pageViews.Count || index == _selectionPage) return;
        var view = _pageViews[index];
        var reference = view.Reference;
        string query = _query;
        try
        {
            var rects = await Task.Run(() =>
            {
                using var pdf = UglyToad.PdfPig.PdfDocument.Open(_working);
                string text = ContentOrderTextExtractor.GetText(pdf.GetPage(reference.SourcePage + 1));
                return PdfSearchGeometryReader.Find(_working, reference.SourcePage, query, text, reference.Rotation, _lifetime.Token);
            });
            if (ReferenceEquals(view.Reference, reference) && _query == query)
                view.SetHighlights(rects, Color.FromArgb(110, 0xFB, 0xBA, 0x00));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or OperationCanceledException) { }
    }

    // ------------------------------------------------------------------ save, print, document operations

    private async Task<string?> AskSavePathAsync(string title, string suggested)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return null;
        var start = await storage.TryGetFolderFromPathAsync(Path.GetDirectoryName(_original)!);
        var chosen = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggested,
            SuggestedStartLocation = start,
            DefaultExtension = "pdf",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }]
        });
        if (chosen is null || !chosen.Path.IsFile) return null;
        string path = chosen.Path.LocalPath;
        if (string.Equals(Path.GetFullPath(path), _original, StringComparison.OrdinalIgnoreCase))
        {
            if (Owner is not null)
                await GetDialogs.InfoAsync(Owner, "Выберите другое имя",
                    "Исходный файл не перезаписывается: так оригинал всегда остаётся целым. Сохраните изменения под новым именем.",
                    GetDialogs.Tone.Warning);
            return null;
        }
        if (File.Exists(path))
        {
            try { File.Delete(path); } // the user confirmed overwrite of that other file in the dialog
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _status("Файл занят или защищён: " + ex.Message);
                return null;
            }
        }
        return path;
    }

    public async Task SaveAsAsync()
    {
        string? target = await AskSavePathAsync("Сохранить PDF", Path.GetFileNameWithoutExtension(_original) + "_изм.pdf");
        if (target is null) return;
        try
        {
            var pages = _plan.Snapshot();
            await Task.Run(() => PdfDocumentWriter.SaveAs(_working, target, pages, _lifetime.Token));
            _savedFingerprint = Fingerprint();
            UpdateState();
            _status($"Сохранено: {Path.GetFileName(target)}. Оригинал не изменён.");
            await _openPath(target);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or
                                   ArgumentException or UnauthorizedAccessException)
        {
            _status("Не удалось сохранить: " + ex.Message);
        }
    }

    /// <summary>PDF for printing: the original when nothing changed, otherwise a temporary copy with all edits.</summary>
    public async Task<string?> PreparePrintableAsync()
    {
        if (!_plan.HasChanges) return _original;
        string copy = Path.Combine(_sessionFolder, "print-" + Guid.NewGuid().ToString("N")[..8] + ".pdf");
        try
        {
            var pages = _plan.Snapshot();
            await Task.Run(() => PdfDocumentWriter.SaveAs(_working, copy, pages, _lifetime.Token));
            return copy;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or ArgumentException)
        {
            _status("Не удалось подготовить документ к печати: " + ex.Message);
            return null;
        }
    }

    private async Task ExtractAsync()
    {
        if (Owner is null) return;
        string? spec = await GetDialogs.AskTextAsync(Owner, "Извлечь страницы",
            "Номера страниц нового PDF, например: 1-3, 5, 8. Порядок сохраняется как указан.",
            (_current + 1).ToString(CultureInfo.InvariantCulture), 200, "Далее");
        if (spec is null) return;
        IReadOnlyList<PdfPageReference> pages;
        try { pages = PdfPageRange.Parse(spec, _plan.Pages); }
        catch (FormatException ex) { _status(ex.Message); return; }
        string? target = await AskSavePathAsync("Сохранить извлечённые страницы",
            Path.GetFileNameWithoutExtension(_original) + "_стр.pdf");
        if (target is null) return;
        try
        {
            await Task.Run(() => PdfDocumentWriter.SaveAs(_working, target, pages, _lifetime.Token));
            _status($"Создан PDF из {pages.Count} стр.: {Path.GetFileName(target)}.");
            await _openPath(target);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or ArgumentException)
        { _status("Не удалось извлечь страницы: " + ex.Message); }
    }

    private async Task<IReadOnlyList<string>> PickPdfsAsync(string title, bool multiple)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return [];
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = multiple,
            FileTypeFilter = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }]
        });
        return files.Where(f => f.Path.IsFile).Select(f => f.Path.LocalPath).ToArray();
    }

    private async Task InsertFromPdfAsync()
    {
        var files = await PickPdfsAsync("Вставить страницы из PDF", multiple: false);
        if (files.Count == 0) return;
        string next = Path.Combine(_sessionFolder, "source-" + Guid.NewGuid().ToString("N")[..8] + ".pdf");
        try
        {
            string current = _working;
            int before = _raster.PageSizes.Count;
            await Task.Run(() => PdfDocumentWriter.AppendSourcePages(current, files[0], next));
            var raster = await Task.Run(() => new PdfRaster(next));
            int added = raster.PageSizes.Count - before;
            _working = next;
            _raster = raster;
            _markupAllowed = PdfMarkupPolicy.ReadEligibleSourcePages(_working);
            int at = _current;
            Change(p =>
            {
                for (int i = 0; i < added; i++)
                    p.InsertSourcePageAfter(at + i, p.RegisterSourcePage());
            }, $"Вставлено страниц: {added} после страницы {at + 1}.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or ArgumentException)
        { _status("Не удалось вставить страницы: " + ex.Message); }
    }

    private async Task MergeAsync()
    {
        var files = await PickPdfsAsync("Выберите PDF для объединения (по порядку)", multiple: true);
        if (files.Count == 0) return;
        string? target = await AskSavePathAsync("Сохранить объединённый PDF",
            Path.GetFileNameWithoutExtension(_original) + "_объединённый.pdf");
        if (target is null) return;
        try
        {
            var pages = _plan.Snapshot();
            await Task.Run(() => PdfDocumentWriter.Merge(_working, pages, files, target, _lifetime.Token));
            _status($"Объединено файлов: {files.Count + 1}. Результат: {Path.GetFileName(target)}.");
            await _openPath(target);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or ArgumentException)
        { _status("Не удалось объединить: " + ex.Message); }
    }

    private async Task NumberPagesAsync()
    {
        if (Owner is null) return;
        var format = new ComboBox { ItemsSource = new[] { "1", "1 из 12", "Страница 1 из 12" }, SelectedIndex = 1, MinWidth = 260 };
        var position = new ComboBox { ItemsSource = new[] { "Внизу по центру", "Внизу справа", "Вверху справа" }, SelectedIndex = 0, MinWidth = 260 };
        var start = new TextBox { Text = "1", Width = 100 };
        var skip = new CheckBox { Content = "Без номера на первой странице (титульный лист)" };
        var grid = new StackPanel { Spacing = 8 };
        grid.Children.Add(new TextBlock { Text = "Вид номера" });
        grid.Children.Add(format);
        grid.Children.Add(new TextBlock { Text = "Где ставить" });
        grid.Children.Add(position);
        grid.Children.Add(new TextBlock { Text = "Начать с номера" });
        grid.Children.Add(start);
        grid.Children.Add(skip);
        bool remove = false;
        bool ok = await GetDialogs.FormAsync(Owner, "Нумерация страниц",
            "Номер ставится отдельно от штампа и одинаково выглядит в просмотре, печати и сохранённом PDF.",
            grid, "Пронумеровать",
            () => int.TryParse(start.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int s) && s is >= 1 and <= 9999,
            ("Убрать нумерацию", () => remove = true));
        if (remove)
        {
            Change(p => p.SetPageNumbers(_ => "", PdfPageNumberPosition.BottomCenter), "Нумерация убрана.");
            return;
        }
        if (!ok) return;
        var options = new PdfPageNumberingOptions((PdfPageNumberFormat)format.SelectedIndex,
            (PdfPageNumberPosition)position.SelectedIndex, int.Parse(start.Text!, CultureInfo.InvariantCulture), skip.IsChecked == true);
        int count = _plan.Pages.Count;
        Change(p => p.SetPageNumbers(i => PdfPageNumbering.Label(i, count, options), options.Position),
            "Страницы пронумерованы. Сохраните результат через «Сохранить как…».");
    }

    private async Task CompressAsync()
    {
        if (HasUnsavedChanges)
        {
            _status("Сначала сохраните изменения в новый PDF и откройте его — сжатие работает с сохранённым файлом.");
            return;
        }
        string? target = await AskSavePathAsync("Сохранить сжатую копию", Path.GetFileNameWithoutExtension(_original) + "_сжатый.pdf");
        if (target is null) return;
        _status("Сжимаем без потери качества…");
        try
        {
            var (before, after) = await PdfLosslessOptimizer.OptimizeAsync(_original, target, _plan.Pages.Count, _lifetime.Token);
            static string Size(long b) => b >= 1024 * 1024 ? $"{b / (1024.0 * 1024):0.#} МБ" : $"{Math.Max(1, (b + 512) / 1024)} КБ";
            int saved = before > 0 ? (int)Math.Round(100.0 * (before - after) / before) : 0;
            _status($"Сжатая копия «{Path.GetFileName(target)}»: {Size(before)} → {Size(after)} (−{saved} %). Исходный файл не изменён.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or TimeoutException)
        {
            if (Owner is not null) await GetDialogs.InfoAsync(Owner, "Сжатие PDF", ex.Message, GetDialogs.Tone.Warning);
        }
    }

    private async Task CheckSignaturesAsync()
    {
        if (Owner is null) return;
        _status("Проверяем подписи…");
        try
        {
            var checks = await Task.Run(() => PdfSignatureInspector.VerifyOffline(_original, _lifetime.Token));
            if (checks.Count == 0)
            {
                await GetDialogs.InfoAsync(Owner, "Подписи", "В документе нет электронных подписей.");
                return;
            }
            var text = string.Join("\n\n", checks.Select(c => $"Подпись {c.Sequence}: {c.ToRussianSummary()}"));
            var tone = checks.Any(c => c.State == PdfSignatureTrust.Failed) ? GetDialogs.Tone.Error
                : checks.All(c => c.State == PdfSignatureTrust.LocallyTrusted) ? GetDialogs.Tone.Success : GetDialogs.Tone.Warning;
            await GetDialogs.InfoAsync(Owner, "Проверка подписей",
                text + "\n\nПроверка автономная: без доступа к спискам отзыва и меткам времени. Юридическую значимость подтверждает удостоверяющий центр.", tone);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            await GetDialogs.InfoAsync(Owner, "Проверка подписей", "Не удалось проверить подписи: " + ex.Message, GetDialogs.Tone.Warning);
        }
    }

    private async Task FillFormAsync()
    {
        if (Owner is null) return;
        IReadOnlyList<string> fields;
        try { fields = await Task.Run(() => PdfTextFormService.ReadEditableFieldNames(_original)); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or InvalidOperationException or IOException)
        {
            await GetDialogs.InfoAsync(Owner, "Заполнение формы",
                "Это не PDF-форма или форма не поддерживается: заполнять можно только документы с текстовыми полями " +
                "(без подписей, XFA и скриптов). " + ex.Message);
            return;
        }
        if (fields.Count == 0)
        {
            await GetDialogs.InfoAsync(Owner, "Заполнение формы", "В документе нет текстовых полей формы.");
            return;
        }
        var boxes = new Dictionary<string, TextBox>();
        var list = new StackPanel { Spacing = 6 };
        foreach (string name in fields.Take(40))
        {
            list.Children.Add(new TextBlock { Text = name });
            var box = new TextBox { MinWidth = 420 };
            boxes[name] = box;
            list.Children.Add(box);
        }
        var scroll = new ScrollViewer { Content = list, MaxHeight = 420 };
        if (!await GetDialogs.FormAsync(Owner, "Заполнение формы", "Заполненная копия сохраняется отдельным файлом.", scroll, "Сохранить копию…"))
            return;
        var values = boxes.Where(kv => !string.IsNullOrWhiteSpace(kv.Value.Text))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Text!.Trim());
        string? target = await AskSavePathAsync("Сохранить заполненную форму", Path.GetFileNameWithoutExtension(_original) + "_заполнено.pdf");
        if (target is null) return;
        try
        {
            await Task.Run(() => PdfTextFormService.FillNewCopy(_original, target, values, _lifetime.Token));
            _status("Форма заполнена: " + Path.GetFileName(target));
            await _openPath(target);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or ArgumentException or NotSupportedException)
        { await GetDialogs.InfoAsync(Owner, "Заполнение формы", ex.Message, GetDialogs.Tone.Warning); }
    }

    private async Task CompareAsync()
    {
        if (Owner is null) return;
        var other = await PickPdfsAsync("С каким PDF сравнить", multiple: false);
        if (other.Count == 0) return;
        _status("Сравниваем текст…");
        try
        {
            var report = await Task.Run(() => PdfTextComparisonService.CompareFiles(_original, other[0], _lifetime.Token));
            int changed = report.Changes.Count(c => c.Type == PdfTextComparisonService.Kind.ChangedText);
            int added = report.Changes.Count(c => c.Type == PdfTextComparisonService.Kind.Added);
            int removed = report.Changes.Count(c => c.Type == PdfTextComparisonService.Kind.Removed);
            int images = report.Changes.Count(c => c.Type == PdfTextComparisonService.Kind.UnverifiedImage);
            var lines = report.Changes.Where(c => c.Type != PdfTextComparisonService.Kind.EqualText).Take(30)
                .Select(c => c.Type switch
                {
                    PdfTextComparisonService.Kind.ChangedText => $"• стр. {c.LeftPage + 1} ↔ {c.RightPage + 1}: текст отличается",
                    PdfTextComparisonService.Kind.Added => $"• стр. {c.RightPage + 1} есть только во втором файле",
                    PdfTextComparisonService.Kind.Removed => $"• стр. {c.LeftPage + 1} есть только в этом файле",
                    _ => $"• стр. {c.LeftPage + 1}: нет текста (скан) — сравните глазами"
                });
            string summary = changed + added + removed + images == 0
                ? "Текст документов совпадает постранично."
                : $"Отличается страниц: {changed}, добавлено: {added}, удалено: {removed}, без текста: {images}.\n\n" + string.Join("\n", lines);
            await GetDialogs.InfoAsync(Owner, "Сравнение с «" + Path.GetFileName(other[0]) + "»", summary,
                changed + added + removed == 0 ? GetDialogs.Tone.Success : GetDialogs.Tone.Warning);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        { await GetDialogs.InfoAsync(Owner, "Сравнение", ex.Message, GetDialogs.Tone.Warning); }
    }
}
