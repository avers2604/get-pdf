using Avalonia;
using Avalonia.Controls;
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
    private readonly string _pdfPath;
    private readonly Action _closeTab;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ComboBox _printer = new() { Width = 350 };
    private readonly ComboBox _paper = new() { Width = 190,
        ItemsSource = new[] { "A4 — 210 × 297 мм", "A3 — 297 × 420 мм" }, SelectedIndex = 0 };
    private readonly ComboBox _sides = new() { Width = 280,
        ItemsSource = new[] { "Односторонняя", "Двусторонняя — длинный край",
            "Двусторонняя — короткий край" }, SelectedIndex = 0 };
    private readonly TextBox _pages = new() { Width = 270,
        Watermark = "Все страницы или 1,3-5" };
    private readonly TextBox _copies = new() { Width = 140, Text = "1" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _stepTitle = new() { FontSize = 20,
        FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _previewStatus = new() { Text = "Загрузка предпросмотра…" };
    private readonly Image _preview = new() { Width = 235, Height = 325,
        Stretch = Stretch.Uniform };
    private readonly StackPanel _stage = new() { Spacing = 14 };
    private readonly Button _back = new() { Content = "Назад" };
    private readonly Button _next = new() { Content = "Далее", IsEnabled = false };
    private readonly Button _cancel = new() { Content = "Закрыть" };
    private Bitmap? _bitmap;
    private int _pageCount;
    private int _step = 1;
    private bool _busy;
    private bool _disposed;

    public PrintWizardPane(string pdfPath, Action closeTab)
    {
        _pdfPath = pdfPath;
        _closeTab = closeTab;
        var left = new StackPanel { Spacing = 18, Width = 680 };
        left.Children.Add(new TextBlock
        {
            Text = "Мастер печати ГЭТ",
            FontSize = 26, FontWeight = FontWeight.SemiBold
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

        var side = new StackPanel { Spacing = 10, Width = 270 };
        side.Children.Add(new TextBlock { Text = "Предпросмотр первой страницы",
            FontWeight = FontWeight.SemiBold });
        side.Children.Add(new Border
        {
            Padding = new Thickness(10),
            Background = new SolidColorBrush(Color.Parse("#EFF2F6")),
            Child = _preview
        });
        side.Children.Add(_previewStatus);
        side.Children.Add(new TextBlock
        {
            Text = "Предпросмотр документа. Фактические поля и поддержку A3/дуплекса определяет драйвер принтера.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12
        });

        var columns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 28 };
        columns.Children.Add(left);
        columns.Children.Add(side);
        Content = new ScrollViewer { Content = new Border
            { Padding = new Thickness(28), Child = columns } };
        SizeChanged += (_, eventArgs) =>
            columns.Orientation = eventArgs.NewSize.Width < 1080
                ? Orientation.Vertical : Orientation.Horizontal;

        _back.Click += (_, _) => { if (_step > 1 && !_busy) { _step--; RenderStage(); } };
        _next.Click += async (_, _) => await AdvanceAsync();
        _cancel.Click += (_, _) => _closeTab();

        RenderStage();
        _ = InitializeAsync();
    }

    private static StackPanel Labeled(string text, Control input)
    {
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
        _next.IsEnabled = !_busy && _pageCount > 0 &&
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
            _stage.Children.Add(Labeled("Режим печати", _sides));
        }
        else
        {
            if (TryBuildPlan(out var plan))
                _summary.Text = $"Принтер: {plan!.PrinterName}\n" +
                    $"Страницы: {plan.CupsPageRange}\n" +
                    $"Копии: {plan.Copies}; всего страниц отпечатков: {plan.PageCountToPrint}\n" +
                    $"Бумага: {plan.Paper}; режим: {DuplexText(plan.Duplex)}\n" +
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
                (PrintDuplex)_sides.SelectedIndex, (PrintPaper)_paper.SelectedIndex);
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
        if (_disposed || _busy || _pageCount < 1) return;
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
        RenderStage();
        _status.Text = "Задание передаётся в выбранную очередь печати…";
        try
        {
            string confirmation = await LocalPrintBroker.SubmitPdfAsync(
                _pdfPath, plan!, _lifetime.Token);
            if (!_disposed)
            {
                _status.Text = confirmation;
                _next.IsEnabled = false; // A second click must not duplicate copies.
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_disposed)
                _status.Text = "Печать не подтверждена. Проверьте службу, принтер и поддерживаемые функции. " +
                               "Повторную отправку выполняйте после проверки очереди: задание могло быть принято.";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task InitializeAsync()
    {
        await LoadPrintersAsync();
        if (_disposed) return;
        try
        {
            var info = await Task.Run(() => RenderFirstPage(
                _pdfPath, _lifetime.Token), _lifetime.Token);
            if (_disposed) return;
            _pageCount = info.Pages;
            using var buffer = new MemoryStream(info.PreviewPng, writable: false);
            _bitmap = new Bitmap(buffer);
            _preview.Source = _bitmap;
            _previewStatus.Text = $"Страниц в PDF: {_pageCount}";
            RenderStage();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_disposed)
                _status.Text = "Не удалось открыть PDF для печати. Файл может быть защищён или повреждён.";
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
            string? selected = _printer.SelectedItem as string;
            _printer.ItemsSource = printers;
            _printer.SelectedItem = selected is not null && printers.Contains(selected)
                ? selected : printers.FirstOrDefault();
            _status.Text = printers.Count == 0
                ? "В системе нет доступных принтеров. Добавьте принтер через администратора."
                : "Очереди печати загружены.";
            RenderStage();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_disposed)
            {
                _status.Text = "Служба печати недоступна или не установлен компонент Windows/CUPS.";
                RenderStage();
            }
        }
    }

    private static (int Pages, byte[] PreviewPng) RenderFirstPage(
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
            source.Position = 0;
            var size = Conversion.GetPageSize(source, page: 0, leaveOpen: true);
            var raster = PdfPreviewPlan.GetRasterSize(size.Width, size.Height, 650);
            source.Position = 0;
            using var output = new MemoryStream();
            Conversion.SavePng(output, source, page: 0, leaveOpen: true,
                options: new RenderOptions(Width: raster.Width, Height: raster.Height,
                    WithAnnotations: true, WithFormFill: true));
            cancel.ThrowIfCancellationRequested();
            return (pages, output.ToArray());
        }
        finally { PdfNativeGate.Instance.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _preview.Source = null;
        _bitmap?.Dispose();
        // Active worker cancellation is cooperative; job may already be in spool.
    }
}
