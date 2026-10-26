using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GetDocument.Core.Printing;
using GetDocument.Core.Preview;
using PDFtoImage;

namespace GetDocument.Desktop.Printing;

/// <summary>
/// GET's own tabbed 3-step printing wizard. No OS print dialog is invoked.
/// The installed local spooler still owns the actual printer/driver job.
/// </summary>
internal sealed class PrintWizardPane : UserControl, IDisposable
{
    private static SolidColorBrush Palette(string hex) => new(Color.Parse(hex));

    private static void StyleButton(Button button, bool primary = false)
    {
        button.MinHeight = 44;
        button.Padding = new Thickness(16, 0);
        button.Background = Palette(primary ? "#24305E" : "#FFFFFF");
        button.Foreground = Palette(primary ? "#FFFFFF" : "#141A33");
        button.BorderBrush = Palette(primary ? "#24305E" : "#7E859E");
        button.BorderThickness = new Thickness(1);
    }

    private static void StyleField(TemplatedControl control)
    {
        control.MinHeight = 44;
        control.Background = Palette("#FFFFFF");
        control.Foreground = Palette("#141A33");
        control.BorderBrush = Palette("#7E859E");
        control.BorderThickness = new Thickness(1);
        control.Padding = new Thickness(10, 8);
        control.CornerRadius = new CornerRadius(9);
    }

    private string _pdfPath;
    private PrintFileSnapshot? _printSnapshot;
    private readonly string? _ownedInputPath;
    private readonly Action _closeTab;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ComboBox _printer = new() { Width = 350 };
    private readonly ComboBox _paper = new() { Width = 190,
        ItemsSource = new[] { "A4 — 210 × 297 мм", "A3 — 297 × 420 мм" }, SelectedIndex = 0 };
    private readonly ComboBox _sides = new() { Width = 280,
        ItemsSource = new[] { "Односторонняя", "Двусторонняя — длинный край",
            "Двусторонняя — короткий край" }, SelectedIndex = 0 };
    private readonly ComboBox _orientation = new() { Width = 250,
        ItemsSource = new[] { "Автоматически (по документу)", "Книжная", "Альбомная" },
        SelectedIndex = 0 };
    private readonly ComboBox _color = new() { Width = 250,
        ItemsSource = new[] { "Настройка принтера", "Цветная", "Чёрно-белая" },
        SelectedIndex = 0 };
    private readonly ComboBox _nup = new() { Width = 270,
        ItemsSource = new[] { "1 страница на листе", "2 страницы на листе", "4 страницы на листе" },
        SelectedIndex = 0 };
    private readonly ComboBox _tray = new() { Width = 350 };
    private readonly TextBox _pages = new() { Width = 270,
        PlaceholderText = "Все страницы или 1,3-5" };
    private readonly TextBox _copies = new() { Width = 140, Text = "1" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _stepTitle = new() { Classes = { "title" },
        FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _previewStatus = new() { Text = "Загрузка предпросмотра…" };
    private readonly Grid _sheetPreview = new() { Width = 235, Height = 325 };
    private readonly StackPanel _stage = new() { Spacing = 14 };
    private readonly Button _previousSheet = new() { Content = "‹", Classes = { "ghost" } };
    private readonly Button _nextSheet = new() { Content = "›", Classes = { "ghost" } };
    private readonly TextBlock _sheetLabel = new() { Text = "Лист 1", VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _back = new() { Content = "Назад", Classes = { "ghost" } };
    private readonly Button _next = new() { Content = "Далее", IsEnabled = false, Classes = { "primary" } };
    private readonly Button _cancel = new() { Content = "Закрыть", Classes = { "ghost" } };
    private readonly Dictionary<int, Bitmap> _previewPages = [];
    private int _sheetIndex;
    private CancellationTokenSource? _sheetLoad;
    private int _pageCount;
    private int _step = 1;
    private bool _busy;
    private bool _submitted;
    private bool _disposed;
    private int _trayRequest;

    public PrintWizardPane(string pdfPath, Action closeTab, bool deleteOwnedInput = false)
    {
        _pdfPath = pdfPath;
        _closeTab = closeTab;
        // Only an Office-generated PDF in the known print export directory is
        // deletable by this pane. Source/user PDFs must NEVER be deleted.
        if (deleteOwnedInput && OfficePrintFiles.IsOwnedExport(pdfPath))
            _ownedInputPath = Path.GetFullPath(pdfPath);
        Background = Palette("#F4F5F9");
        Foreground = Palette("#141A33");
        StyleField(_printer);
        StyleField(_paper);
        StyleField(_sides);
        StyleField(_orientation);
        StyleField(_color);
        StyleField(_nup);
        StyleField(_tray);
        StyleField(_pages);
        StyleField(_copies);
        StyleButton(_back);
        StyleButton(_next, primary: true);
        StyleButton(_cancel);
        StyleButton(_previousSheet);
        StyleButton(_nextSheet);
        _stepTitle.Foreground = Palette("#24305E");
        _summary.Foreground = Palette("#141A33");
        _status.Foreground = Palette("#565C7B");
        _previewStatus.Foreground = Palette("#565C7B");
        var left = new StackPanel { Spacing = 18 };
        left.Children.Add(new TextBlock
        {
            Text = "Мастер печати ГЭТ",
            Classes = { "title" }, FontSize = 26, FontWeight = FontWeight.SemiBold,
            Foreground = Palette("#24305E")
        });
        left.Children.Add(new TextBlock
        {
            Text = "Настройки выбираются здесь. Системное окно печати не открывается.",
            TextWrapping = TextWrapping.Wrap
        });
        left.Children.Add(_stepTitle);
        left.Children.Add(_stage);
        left.Children.Add(_summary);
        left.Children.Add(_status);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        controls.Children.Add(_back);
        controls.Children.Add(_next);
        controls.Children.Add(_cancel);
        left.Children.Add(controls);
        var leftCard = new Border
        {
            MinWidth = 520,
            MaxWidth = 720,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Palette("#FFFFFF"),
            BorderBrush = Palette("#E1E4EE"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(24),
            Child = left
        };

        var side = new StackPanel { Spacing = 10, Width = 345 };
        side.Children.Add(new TextBlock { Text = "Предпросмотр размещения на листе",
            FontWeight = FontWeight.SemiBold });
        side.Children.Add(new Border
        {
            Padding = new Thickness(10),
            Background = Palette("#EEF0F6"),
            BorderBrush = Palette("#E1E4EE"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = _sheetPreview
        });
        var sheetNav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        sheetNav.Children.Add(_previousSheet);
        sheetNav.Children.Add(_sheetLabel);
        sheetNav.Children.Add(_nextSheet);
        side.Children.Add(sheetNav);
        side.Children.Add(_previewStatus);
        side.Children.Add(new TextBlock
        {
            Text = "Показаны реальные первые страницы PDF в примерной раскладке. Фактические поля, " +
                   "поворот и масштабирование могут отличаться у драйвера; диапазон страниц " +
                   "на предпросмотр не влияет.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12
        });

        var sideCard = new Border
        {
            Background = Palette("#FFFFFF"),
            BorderBrush = Palette("#E1E4EE"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18),
            Child = side
        };
        var columns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 28 };
        columns.Children.Add(leftCard);
        columns.Children.Add(sideCard);
        Content = new ScrollViewer { Content = new Border
            { Padding = new Thickness(28), Child = columns } };
        SizeChanged += (_, eventArgs) =>
            columns.Orientation = eventArgs.NewSize.Width < 1080
                ? Orientation.Vertical : Orientation.Horizontal;

        _back.Click += (_, _) => { if (_step > 1 && !_busy) { _step--; RenderStage(); } };
        _next.Click += async (_, _) => await AdvanceAsync();
        _printer.SelectionChanged += async (_, _) => await LoadTraysAsync();
        _paper.SelectionChanged += (_, _) => RenderSheetPreview();
        _orientation.SelectionChanged += (_, _) => RenderSheetPreview();
        _nup.SelectionChanged += (_, _) => { _sheetIndex = 0; RenderSheetPreview(); };
        _pages.TextChanged += (_, _) => { _sheetIndex = 0; RenderSheetPreview(); };
        _previousSheet.Click += (_, _) => { if (_sheetIndex > 0) { _sheetIndex--; RenderSheetPreview(); } };
        _nextSheet.Click += (_, _) => { _sheetIndex++; RenderSheetPreview(); };
        _cancel.Click += (_, _) => _closeTab();

        RenderStage();
        _ = InitializeAsync();
    }

    private static StackPanel Labeled(string text, Control input)
    {
        // The step is rebuilt on every change while the inputs are reused: detach an input from
        // the previous step's panel first (Avalonia refuses a control with two visual parents).
        if (input.Parent is Panel previous) previous.Children.Remove(input);
        var result = new StackPanel { Spacing = 5 };
        result.Children.Add(new TextBlock { Text = text, FontWeight = FontWeight.SemiBold });
        result.Children.Add(input);
        return result;
    }

    private void RenderStage()
    {
        if (_disposed) return;
        _stage.Children.Clear();
        _summary.Text = "";
        _back.IsEnabled = !_busy && _step > 1;
        _next.IsEnabled = !_busy && !_submitted && _pageCount > 0 &&
            _printer.SelectedItem is string;
        _next.Content = _step == 3 ? "Отправить на печать" : "Далее";
        _stepTitle.Text = _step switch
        {
            1 => "Шаг 1 из 3 — выбор принтера",
            2 => "Шаг 2 из 3 — параметры документа",
            _ => "Шаг 3 из 3 — подтверждение"
        };
        if (_step == 1)
        {
            _stage.Children.Add(Labeled("Принтер из локальной очереди", _printer));
            var refresh = new Button { Content = "Обновить список принтеров" };
            StyleButton(refresh);
            refresh.Click += async (_, _) => await LoadPrintersAsync();
            _stage.Children.Add(refresh);
            _stage.Children.Add(new TextBlock
            {
                Text = "Печать доступна только на принтерах, установленных и разрешённых на этом компьютере.",
                TextWrapping = TextWrapping.Wrap
            });
        }
        else if (_step == 2)
        {
            _stage.Children.Add(Labeled(
                $"Страницы (в документе {_pageCount})", _pages));
            _stage.Children.Add(Labeled("Копии (от 1 до 99)", _copies));
            _stage.Children.Add(Labeled("Размер бумаги", _paper));
            _stage.Children.Add(Labeled("Источник бумаги / лоток", _tray));
            _stage.Children.Add(Labeled("Режим печати", _sides));
            _stage.Children.Add(Labeled("Ориентация", _orientation));
            _stage.Children.Add(Labeled("Цветность", _color));
            _stage.Children.Add(Labeled("Страниц на листе", _nup));
            _stage.Children.Add(new TextBlock
            {
                Text = "2/4 страницы на листе доступны только при выборе всех страниц. " +
                    "Возможности проверяются по драйверу; неподдерживаемые настройки запрещены.",
                TextWrapping = TextWrapping.Wrap, FontSize = 12
            });
        }
        else
        {
            if (TryBuildPlan(out var plan))
                _summary.Text = $"Принтер: {plan!.PrinterName}\n" +
                    $"Страницы: {plan.CupsPageRange}\n" +
                    $"Копии: {plan.Copies}; всего страниц отпечатков: {plan.PageCountToPrint}\n" +
                    $"Бумага: {plan.Paper}; режим: {DuplexText(plan.Duplex)}\n" +
                    $"Ориентация: {plan.Orientation}; цвет: {plan.Color}\n" +
                    $"На листе: {(int)plan.PagesPerSheet}; печатных сторон: {plan.SheetSidesToPrint}\n" +
                    $"Лоток: {plan.Tray?.Label ?? "Устройство по умолчанию"}\n" +
                    $"Ориентировочно физических листов: {plan.EstimatedPhysicalSheets}\n" +
                    "Проверьте бумагу, права и готовность принтера перед отправкой.";
        }
    }

    private static string DuplexText(PrintDuplex duplex) => duplex switch
    {
        PrintDuplex.LongEdge => "Двусторонняя (длинный край)",
        PrintDuplex.ShortEdge => "Двусторонняя (короткий край)",
        _ => "Односторонняя"
    };

    private bool TryBuildPlan(out PrintJobPlan? result)
    {
        result = null;
        try
        {
            if (_printer.SelectedItem is not string printer)
                throw new ArgumentException("Сначала выберите принтер.");
            if (!int.TryParse(_copies.Text, out int count))
                throw new ArgumentException("Количество копий должно быть целым числом.");
            result = PrintJobPlan.Create(printer, _pageCount, _pages.Text, count,
                (PrintDuplex)_sides.SelectedIndex, (PrintPaper)_paper.SelectedIndex,
                (PrintOrientation)_orientation.SelectedIndex,
                (PrintColorMode)_color.SelectedIndex,
                _nup.SelectedIndex switch
                {
                    0 => PrintPagesPerSheet.One,
                    1 => PrintPagesPerSheet.Two,
                    2 => PrintPagesPerSheet.Four,
                    _ => (PrintPagesPerSheet)(-1)
                },
                (_tray.SelectedItem as ComboBoxItem)?.Tag as PrintTrayOption);
            return true;
        }
        catch (ArgumentException ex)
        {
            _status.Text = ex.Message;
            return false;
        }
    }

    private async Task AdvanceAsync()
    {
        if (_disposed || _busy || _submitted || _pageCount < 1) return;
        _status.Text = "";
        if (_step == 1)
        {
            if (_printer.SelectedItem is null)
            {
                _status.Text = "Не выбран принтер.";
                return;
            }
            _step = 2;
            RenderStage();
            return;
        }
        if (!TryBuildPlan(out var plan)) return;
        if (_step == 2)
        {
            _step = 3;
            RenderStage();
            return;
        }
        _busy = true;
        _submitted = true; // Outcome may be ambiguous if queue accepts then connection times out.
        RenderStage();
        _status.Text = "Задание передаётся в выбранную очередь печати…";
        try
        {
            if (_printSnapshot is not { } snapshot ||
                !await snapshot.VerifyAsync(_lifetime.Token))
            {
                if (!_disposed)
                    _status.Text = "Подготовленная PDF-копия изменилась. Печать отменена; откройте мастер заново.";
                return;
            }
            string confirmation = await LocalPrintBroker.SubmitPdfAsync(
                snapshot.Path, plan!, _lifetime.Token, snapshot.Sha256Hex);
            if (!_disposed)
            {
                _status.Text = confirmation;
                _next.IsEnabled = false; // Strictly one attempt per wizard instance.
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (PrintSubmissionException ex)
        {
            if (!_disposed)
                _status.Text = ex.Failure.UserMessage +
                    (ex.Failure.MightBeQueued
                        ? " Не отправляйте задание снова, пока не проверите очередь."
                        : " Исправьте причину и откройте новый мастер печати.");
        }
        catch (InvalidDataException)
        {
            if (!_disposed)
                _status.Text = "Подготовленная PDF-копия не прошла проверку целостности. Печать прекращена.";
        }
        catch (Exception)
        {
            if (!_disposed)
                _status.Text = "Служба печати не подтвердила результат. " +
                    "Проверьте очередь до повторного задания.";
        }
        finally
        {
            _busy = false;
        }
    }

    private void RenderSheetPreview()
    {
        if (_disposed || _pageCount == 0) return;
        _sheetLoad?.Cancel();

        PrintPagesPerSheet count = _nup.SelectedIndex switch
        {
            1 => PrintPagesPerSheet.Two,
            2 => PrintPagesPerSheet.Four,
            _ => PrintPagesPerSheet.One
        };
        var orientation = (PrintOrientation)Math.Max(0, _orientation.SelectedIndex);
        PrintSheetPreviewLayout layout;
        try
        {
            // Same PrintJobPlan policy and page-range grammar as actual spool.
            // Page order and 2/4-up restrictions must never diverge.
            var selection = PrintJobPlan.Create(
                _printer.SelectedItem as string ?? "Предпросмотр",
                _pageCount, _pages.Text, 1, PrintDuplex.OneSided, PrintPaper.A4,
                orientation, PrintColorMode.PrinterDefault, count);
            int pagesPerSheet = (int)count;
            int total = (selection.Pages.Count + pagesPerSheet - 1) / pagesPerSheet;
            _sheetIndex = Math.Clamp(_sheetIndex, 0, total - 1);
            layout = PrintSheetPreviewPlan.Create(
                _pageCount, selection.Pages, count, orientation, _sheetIndex);
        }
        catch (ArgumentException)
        {
            _sheetPreview.Children.Clear();
            _sheetLabel.Text = "Диапазон не определён";
            _previousSheet.IsEnabled = false;
            _nextSheet.IsEnabled = false;
            _previewStatus.Text = "Уточните диапазон страниц. Предыдущий макет скрыт.";
            return;
        }

        bool landscape = orientation == PrintOrientation.Landscape;
        _sheetPreview.Width = landscape ? 325 : 235;
        _sheetPreview.Height = landscape ? 235 : 325;
        _sheetPreview.Children.Clear();
        _sheetPreview.RowDefinitions.Clear();
        _sheetPreview.ColumnDefinitions.Clear();
        for (int i = 0; i < layout.Rows; i++)
            _sheetPreview.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        for (int i = 0; i < layout.Columns; i++)
            _sheetPreview.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        _sheetLabel.Text = $"Лист {layout.SheetIndex + 1} из {layout.TotalSheets}";
        _previousSheet.IsEnabled = layout.SheetIndex > 0;
        _nextSheet.IsEnabled = layout.SheetIndex < layout.TotalSheets - 1;
        _previewStatus.Text = "Показаны выбранные страницы PDF. Поля и компоновка принтера могут отличаться.";

        int cellHeight = (landscape ? 235 : 325) / layout.Rows;
        int cellWidth = (landscape ? 325 : 235) / layout.Columns;
        var missing = new List<int>();
        for (int index = 0; index < layout.PageNumbers.Count; index++)
        {
            int page = layout.PageNumbers[index];
            var inner = new StackPanel { Spacing = 3 };
            inner.Children.Add(new TextBlock
            {
                Text = $"Стр. {page}", FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            if (_previewPages.TryGetValue(page, out var thumbnail))
            {
                inner.Children.Add(new Image
                {
                    Source = thumbnail, Stretch = Stretch.Uniform,
                    MaxWidth = Math.Max(50, cellWidth - 22),
                    MaxHeight = Math.Max(45, cellHeight - 33)
                });
            }
            else
            {
                missing.Add(page);
                inner.Children.Add(new TextBlock
                {
                    Text = "Загрузка страницы…", TextWrapping = TextWrapping.Wrap,
                    FontSize = 11
                });
            }
            var tile = new Border
            {
                Margin = new Thickness(3), Padding = new Thickness(4),
                Background = Palette("#FFFFFF"), BorderBrush = Palette("#BBC4D8"),
                BorderThickness = new Thickness(1), Child = inner
            };
            Grid.SetRow(tile, index / layout.Columns);
            Grid.SetColumn(tile, index % layout.Columns);
            _sheetPreview.Children.Add(tile);
        }
        // Keep the initial 1..4 thumbnails and only the currently visible sheet.
        // Paging through a 1,000-page PDF must not accumulate 1,000 bitmaps.
        var keep = new HashSet<int>(layout.PageNumbers);
        foreach (int page in _previewPages.Keys.ToArray())
        {
            if (page <= 4 || keep.Contains(page)) continue;
            if (_previewPages.Remove(page, out var old)) old.Dispose();
        }
        if (missing.Count > 0) _ = FetchPageThumbnailsAsync(missing);
    }

    private async Task FetchPageThumbnailsAsync(IReadOnlyList<int> numbers)
    {
        var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _sheetLoad = request;
        try
        {
            // A short delay avoids re-rendering PDF for every intermediate
            // keystroke while an operator is editing the page-range field.
            await Task.Delay(180, request.Token);
            var images = await Task.Run(
                () => RenderSelectedPages(_pdfPath, _pageCount, numbers, request.Token),
                request.Token);
            if (_disposed || request.IsCancellationRequested) return;
            foreach (var (page, data) in images)
            {
                if (_previewPages.ContainsKey(page)) continue;
                using var bytes = new MemoryStream(data, writable: false);
                _previewPages[page] = new Bitmap(bytes);
            }
            RenderSheetPreview();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_disposed && !request.IsCancellationRequested)
                _previewStatus.Text = "Миниатюры выбранных страниц недоступны. Печать требует проверки PDF.";
        }
        finally
        {
            if (ReferenceEquals(_sheetLoad, request)) _sheetLoad = null;
            request.Dispose();
        }
    }

    private static IReadOnlyList<(int Page, byte[] Data)> RenderSelectedPages(
        string path, int pageCount, IReadOnlyList<int> numbers, CancellationToken token)
    {
        PdfNativeGate.Instance.Wait(token);
        try
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536, FileOptions.SequentialScan);
            PdfPreviewPlan.ValidateInputLength(source.Length);
            int actualCount = Conversion.GetPageCount(source, leaveOpen: true);
            if (actualCount != pageCount)
                throw new InvalidDataException("Число страниц PDF изменилось после открытия.");
            var thumbnails = new List<(int Page, byte[] Data)>(numbers.Count);
            foreach (int page in numbers)
            {
                token.ThrowIfCancellationRequested();
                PdfPreviewPlan.ValidatePage(page - 1, actualCount);
                source.Position = 0;
                var size = Conversion.GetPageSize(source, page: page - 1, leaveOpen: true);
                var raster = PdfPreviewPlan.GetRasterSize(size.Width, size.Height, 480);
                source.Position = 0;
                using var output = new MemoryStream();
                Conversion.SavePng(output, source, page: page - 1, leaveOpen: true,
                    options: new PDFtoImage.RenderOptions(Width: raster.Width, Height: raster.Height,
                        WithAnnotations: true, WithFormFill: true));
                thumbnails.Add((page, output.ToArray()));
            }
            return thumbnails;
        }
        finally { PdfNativeGate.Instance.Release(); }
    }

    private async Task InitializeAsync()
    {
        await LoadPrintersAsync();
        if (_disposed) return;
        try
        {
            // Freeze exactly the PDF revision currently selected for printing,
            // including unsaved Office/PDF edits exported into a temporary PDF.
            var snapshot = await PrintFileSnapshot.CreateAsync(_pdfPath, _lifetime.Token);
            if (_disposed)
            {
                snapshot.Dispose();
                return;
            }
            _printSnapshot = snapshot;
            _pdfPath = snapshot.Path;
            DeleteOwnedInput();
            var info = await Task.Run(() => RenderPreviewPages(
                snapshot.Path, _lifetime.Token), _lifetime.Token);
            if (_disposed) return;
            _pageCount = info.Pages;
            for (int i = 0; i < info.PreviewPngs.Count; i++)
            {
                using var buffer = new MemoryStream(info.PreviewPngs[i], writable: false);
                _previewPages.Add(i + 1, new Bitmap(buffer));
            }
            _previewStatus.Text = $"Всего страниц PDF: {_pageCount}.";
            RenderSheetPreview();
            RenderStage();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _status.Text = "Не удалось открыть PDF для печати. Файл может быть защищён или повреждён. (" + ex.Message + ")";
                _previewStatus.Text = "Предпросмотр недоступен.";
            }
        }
    }

    private async Task LoadTraysAsync()
    {
        if (_disposed) return;
        int request = Interlocked.Increment(ref _trayRequest);
        string? printer = _printer.SelectedItem as string;
        _tray.ItemsSource = new[]
        {
            new ComboBoxItem { Content = "Выбор принтера (автоматически)" }
        };
        _tray.SelectedIndex = 0;
        if (string.IsNullOrWhiteSpace(printer)) return;
        _tray.IsEnabled = false;
        try
        {
            var choices = await LocalPrintBroker.TraysAsync(printer, _lifetime.Token);
            if (_disposed || request != _trayRequest ||
                _printer.SelectedItem is not string current || current != printer) return;
            var items = new List<ComboBoxItem>
            {
                new() { Content = "Выбор принтера (автоматически)" }
            };
            items.AddRange(choices.Select(option => new ComboBoxItem
            {
                Content = option.Label, Tag = option
            }));
            _tray.ItemsSource = items;
            _tray.SelectedIndex = 0;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_disposed && request == _trayRequest)
                _status.Text = "Список лотков недоступен; используется выбор принтера по умолчанию.";
        }
        finally
        {
            if (!_disposed && request == _trayRequest) _tray.IsEnabled = true;
        }
    }

    private async Task LoadPrintersAsync()
    {
        if (_disposed) return;
        _next.IsEnabled = false;
        _status.Text = "Проверяются локальные очереди печати…";
        try
        {
            var printers = await LocalPrintBroker.PrintersAsync(_lifetime.Token);
            if (_disposed) return;
            string? selected = _printer.SelectedItem as string ?? await LocalPrintBroker.DefaultPrinterAsync(_lifetime.Token);
            _printer.ItemsSource = printers;
            _printer.SelectedItem = selected is not null && printers.Contains(selected)
                ? selected : printers.FirstOrDefault();
            _status.Text = printers.Count == 0
                ? "В системе нет доступных принтеров. Добавьте принтер через администратора."
                : "Очереди печати загружены.";
            RenderStage();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _status.Text = "Служба печати недоступна или не установлен компонент Windows/CUPS. (" + ex.Message + ")";
                RenderStage();
            }
        }
    }

    private static (int Pages, IReadOnlyList<byte[]> PreviewPngs) RenderPreviewPages(
        string path, CancellationToken cancel)
    {
        PdfNativeGate.Instance.Wait(cancel);
        try
        {
            cancel.ThrowIfCancellationRequested();
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536, FileOptions.SequentialScan);
            PdfPreviewPlan.ValidateInputLength(source.Length);
            int pages = Conversion.GetPageCount(source, leaveOpen: true);
            PdfPreviewPlan.ValidatePage(0, pages);
            var images = new List<byte[]>(Math.Min(pages, 4));
            for (int page = 0; page < Math.Min(pages, 4); page++)
            {
                cancel.ThrowIfCancellationRequested();
                source.Position = 0;
                var size = Conversion.GetPageSize(source, page: page, leaveOpen: true);
                var raster = PdfPreviewPlan.GetRasterSize(size.Width, size.Height, 480);
                source.Position = 0;
                using var output = new MemoryStream();
                Conversion.SavePng(output, source, page: page, leaveOpen: true,
                    options: new PDFtoImage.RenderOptions(Width: raster.Width, Height: raster.Height,
                        WithAnnotations: true, WithFormFill: true));
                images.Add(output.ToArray());
            }
            cancel.ThrowIfCancellationRequested();
            return (pages, images);
        }
        finally { PdfNativeGate.Instance.Release(); }
    }

    private void DeleteOwnedInput() =>
        OfficePrintFiles.DeleteOwnedExport(_ownedInputPath);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _sheetLoad?.Cancel();
        _printSnapshot?.Dispose();
        _printSnapshot = null;
        DeleteOwnedInput();
        _sheetPreview.Children.Clear();
        foreach (Bitmap image in _previewPages.Values) image.Dispose();
        _previewPages.Clear();
        // Active worker cancellation is cooperative; job may already be in spool.
    }
}
