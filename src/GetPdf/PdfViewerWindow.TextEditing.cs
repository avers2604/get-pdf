using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NAPS2.Images;

namespace GetPdf;

public partial class PdfViewerWindow
{
    private long _textReadRequest;
    private string? _textReadKey;
    private string? _textEditInput;
    private string? _textEditPageKey;
    private IReadOnlyList<PdfEditableFragment> _editableFragments = [];
    private string CurrentTextPageKey => $"{_sourcePath}:{_pageIndex}:{(_plan is null ? -1 : _plan.Pages[_pageIndex].SourcePage)}";
    private readonly HashSet<Task> _textReadTasks = [];
    private async Task LoadTextFragmentsAsync()
    {
        Task task = LoadTextFragmentsCoreAsync();
        _textReadTasks.Add(task);
        try { await task; } finally { _textReadTasks.Remove(task); }
    }
    private async Task LoadTextFragmentsCoreAsync()
    {
        if (TextEditDetails is null || _sourcePath is null || _plan is null || _sessionFolder is null || _closing) return;
        string key = CurrentTextPageKey;
        if (_textReadKey == key) return;
        _textReadKey = key;
        long request = ++_textReadRequest;
        _editableFragments = []; TextFragmentList.ItemsSource = null; ApplyTextButton.IsEnabled = false;
        TextEditDetails.Text = "Читаем исходный текст…";
        if (!_canModify || _nativeDrafts.Count > 0)
        { TextEditDetails.Text = "Редактирование недоступно для защищённого документа или при наличии эскизов аннотаций."; return; }
        var reference = _plan.Pages[_pageIndex];
        string source = _sourcePath;
        string input = System.IO.Path.Combine(_sessionFolder, "text-input-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            await Task.Run(() => PdfDocumentWriter.SaveAs(source, input, [new PdfPageReference(reference.SourcePage, 0)]));
            var fragments = await PdfSourceTextEditor.ReadFragmentsAsync(input, 0, CancellationToken.None);
            if (request != _textReadRequest || _closing || CurrentTextPageKey != key) return;
            _editableFragments = fragments; _textEditInput = input; _textEditPageKey = key;
            TextFragmentList.ItemsSource = fragments;
            TextEditDetails.Text = fragments.Count == 0
                ? "Исходного текста нет. Для поиска выполните OCR; изображение скана этим инструментом не меняется."
                : "Выберите фрагмент на странице или в списке, затем шрифт новой строки.";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("pdf-text-fragments", ex);
            if (request == _textReadRequest && !_closing)
                TextEditDetails.Text = "Движок не смог безопасно прочитать текст этой страницы. Редактирование недоступно.";
        }
    }
    private void TextFragment_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TextFragmentList.SelectedItem is not PdfEditableFragment fragment || ReplacementTextBox is null) return;
        ReplacementTextBox.Text = fragment.Text;
        TextSizeBox.Text = fragment.FontSize.ToString("0.##", CultureInfo.InvariantCulture);
        TextEditDetails.Text = $"Исходный шрифт: {fragment.Font}. Выберите шрифт новой строки — он будет встроен в PDF.";
        TextFont_Changed(sender, e);
        HighlightEditableFragment(fragment);
    }
    private void TextFont_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ApplyTextButton is not null)
            ApplyTextButton.IsEnabled = !_loading && TextFragmentList.SelectedItem is PdfEditableFragment && TextFontCombo.SelectedItem is ComboBoxItem;
    }
    private void SelectTextAtPointer(MouseButtonEventArgs e)
    {
        if (_plan is null || _editableFragments.Count == 0 || PdfMarkupCanvas.ActualWidth <= 0) return;
        Point point = e.GetPosition(PdfMarkupCanvas);
        double x = point.X / PdfMarkupCanvas.ActualWidth, y = point.Y / PdfMarkupCanvas.ActualHeight;
        (x, y) = _plan.Pages[_pageIndex].Rotation switch
        { 90 => (y, 1 - x), 180 => (1 - x, 1 - y), 270 => (1 - y, x), _ => (x, y) };
        var fragment = _editableFragments.FirstOrDefault(f => x >= f.X - .005 && x <= f.X + f.Width + .005 && y >= f.Y - .01 && y <= f.Y + f.Height + .01);
        if (fragment is not null) { TextFragmentList.SelectedItem = fragment; TextFragmentList.ScrollIntoView(fragment); e.Handled = true; }
    }
    private void HighlightEditableFragment(PdfEditableFragment fragment)
    {
        if (_plan is null) return;
        ShowRectangles(_plan.Pages[_pageIndex]);
        var rect = new Rect(fragment.X, fragment.Y, fragment.Width, fragment.Height);
        rect = _plan.Pages[_pageIndex].Rotation switch
        {
            90 => new(1 - rect.Bottom, rect.X, rect.Height, rect.Width),
            180 => new(1 - rect.Right, 1 - rect.Bottom, rect.Width, rect.Height),
            270 => new(rect.Y, 1 - rect.Right, rect.Height, rect.Width), _ => rect
        };
        var outline = new Rectangle { Width = rect.Width * PageVisual.Width, Height = rect.Height * PageVisual.Height,
            Stroke = Brushes.Teal, StrokeThickness = 1.5, Fill = new SolidColorBrush(Color.FromArgb(30, 0, 130, 143)), IsHitTestVisible = false };
        Canvas.SetLeft(outline, rect.X * PageVisual.Width); Canvas.SetTop(outline, rect.Y * PageVisual.Height);
        PdfMarkupCanvas.Children.Add(outline);
    }
    private async void ApplyText_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _closing || _sourcePath is null || _sessionFolder is null || _context is null || _plan is null || _history is null ||
            _textEditInput is null || _textEditPageKey != CurrentTextPageKey || TextFragmentList.SelectedItem is not PdfEditableFragment fragment ||
            TextFontCombo.SelectedItem is not ComboBoxItem font) return;
        if (!double.TryParse(TextSizeBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double size))
        { TextEditDetails.Text = "Укажите размер шрифта от 4 до 96."; return; }
        _loading = true; ApplyTextButton.IsEnabled = false; SetControls();
        int index = _pageIndex;
        string master = System.IO.Path.Combine(_sessionFolder, "text-master-" + Guid.NewGuid().ToString("N") + ".pdf");
        string edited = System.IO.Path.Combine(_sessionFolder, "text-page-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            StatusText.Text = "Изменяем и проверяем исходный текст PDF…";
            await PdfSourceTextEditor.ReplaceAsync(_textEditInput, 0,
                new(fragment, ReplacementTextBox.Text, font.Content.ToString()!, size, TextColorBox.Text.Trim()), edited, CancellationToken.None);
            await Task.Run(() => PdfDocumentWriter.AppendSourceRevision(_sourcePath, edited, master));
            await StopThumbnailWorkerAsync(); await DocumentView.StopAsync();
            _pageStore?.Dispose(); _sourcePath = master;
            _pageStore = new PdfLazyPageStore(_context, master);
            int newSource = _plan.RegisterSourcePage();
            _history.Execute(p => p.ReplaceSourcePage(index, newSource));
            _markupAllowed = PdfMarkupPolicy.ReadEligibleSourcePages(master);
            ReadContinuousPageSizes();
            _textIndex = null; _textReadKey = null; _searchGeometryCache.Clear(); _continuousFingerprint = null;
            _thumbnailCache?.Dispose(); _thumbnailCache = new PdfLruCache<(int, int), System.Windows.Media.Imaging.BitmapSource>(12, _ => { },
                48L * 1024 * 1024, EstimateBitmapBytes);
            _thumbnailItems = []; _pageIndex = index;
            if (_activeSearchQuery.Length > 0) _textIndex = await Task.Run(() => PdfTextReader.Build(master));
            RefreshList(); PersistPdfDraft();
            TextEditDetails.Text = "Исходный текст изменён. Сохраните PDF отдельной копией.";
            StatusText.Text = "Текст изменён; Ctrl+Z отменяет замену. Исходный файл не изменён.";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("pdf-text-replace", ex);
            TextEditDetails.Text = "Замена не выполнена: " + (ex is ArgumentException or InvalidOperationException or NotSupportedException or System.IO.InvalidDataException ? ex.Message : "Движок не поддержал эту структуру PDF. Результат отклонён.");
            StatusText.Text = "Изменение текста не завершено; исходник не изменён.";
        }
        finally { _loading = false; SetControls(); EnsureThumbnailWorker(); }
    }
}
