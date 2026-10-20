using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GetPdf;

public partial class PdfViewerWindow
{
    private bool _textSelectionMode;
    private bool _textSelectionDragging;
    private PdfWordSelection? _selectedWords;
    private string? _selectionPageKey;
    private int _selectionAnchor = -1;
    private int _selectionEnd = -1;
    private long _selectionRequest;
    private CancellationTokenSource? _selectionCancel;

    private string? CurrentSelectionKey()
    {
        if (_plan is null || _sourcePath is null || _pageIndex < 0 ||
            _pageIndex >= _plan.Pages.Count) return null;
        var reference = _plan.Pages[_pageIndex];
        return $"{_sourcePath}:{reference.SourcePage}:{reference.Rotation}:{_pageIndex}";
    }

    private async void ToggleTextSelection_Click(object sender, RoutedEventArgs e)
    {
        _textSelectionMode = !_textSelectionMode;
        if (!_textSelectionMode)
        {
            ClearTextSelection(resetMode: false);
            StatusText.Text = "Выделение текста отключено.";
            return;
        }
        RectangleModeCheckBox.IsChecked = false;
        NoteModeCheckBox.IsChecked = false;
        _nativeDraftMode = null;
        SelectTextButton.Content = "Выделение: вкл.";
        StatusText.Text = "Выделяйте слова текущей страницы мышью. Ctrl+C копирует выделение.";
        await EnsureSelectionWordsAsync();
    }

    private void ClearTextSelection(bool resetMode)
    {
        ++_selectionRequest;
        _selectionCancel?.Cancel();
        _selectionCancel?.Dispose();
        _selectionCancel = null;
        _selectedWords = null;
        _selectionPageKey = null;
        _selectionAnchor = -1;
        _selectionEnd = -1;
        _textSelectionDragging = false;
        PdfTextSelectionCanvas.Children.Clear();
        CopySelectedTextButton.IsEnabled = false;
        if (resetMode) _textSelectionMode = false;
        SelectTextButton.Content = _textSelectionMode ? "Выделение: вкл." : "Выделить текст";
    }

    internal async Task EnsureSelectionWordsAsync()
    {
        if (!_textSelectionMode || _loading || _closing) return;
        string? key = CurrentSelectionKey();
        if (key is null || key == _selectionPageKey) return;
        ++_selectionRequest;
        _selectionCancel?.Cancel();
        _selectionCancel?.Dispose();
        using var cancel = new CancellationTokenSource();
        _selectionCancel = cancel;
        long request = _selectionRequest;
        _selectionPageKey = key;
        _selectionAnchor = _selectionEnd = -1;
        _selectedWords = null;
        PdfTextSelectionCanvas.Children.Clear();
        CopySelectedTextButton.IsEnabled = false;
        string source = _sourcePath!;
        var page = _plan!.Pages[_pageIndex];
        try
        {
            var selection = await Task.Run(() =>
                PdfTextSelectionReader.Read(source, page.SourcePage,
                    page.Rotation, cancel.Token), cancel.Token);
            if (request != _selectionRequest || !_textSelectionMode || _closing ||
                CurrentSelectionKey() != key) return;
            _selectedWords = selection;
            StatusText.Text = selection.Words.Count == 0
                ? "Текстовый слой недоступен для точного выделения. Для скана используйте OCR."
                : "Проведите мышью по словам PDF, затем нажмите Ctrl+C.";
        }
        catch (OperationCanceledException)
        {
            // Page, PDF or tool changed. The pending result is intentionally dropped.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Failure("pdf-text-selection", ex);
            if (request == _selectionRequest)
                StatusText.Text = "Не удалось прочитать расположение текста этой страницы.";
        }
        finally
        {
            if (ReferenceEquals(_selectionCancel, cancel))
                _selectionCancel = null;
        }
    }

    private void BeginTextSelection(MouseButtonEventArgs e)
    {
        if (_selectedWords is null || !_textSelectionMode || _loading ||
            PdfMarkupCanvas.ActualWidth < 1 || PdfMarkupCanvas.ActualHeight < 1)
            return;
        var point = e.GetPosition(PdfMarkupCanvas);
        int index = _selectedWords.HitTest(
            point.X / PdfMarkupCanvas.ActualWidth, point.Y / PdfMarkupCanvas.ActualHeight);
        if (index < 0) { _selectionAnchor = _selectionEnd = -1; RenderTextSelection(); return; }
        _selectionAnchor = _selectionEnd = index;
        PdfPreviewScroll.Focus(); // Ctrl+C stays in this reader workspace.
        _textSelectionDragging = true;
        PdfMarkupCanvas.CaptureMouse();
        RenderTextSelection();
        e.Handled = true;
    }

    private void UpdateTextSelection(MouseEventArgs e)
    {
        if (!_textSelectionDragging || _selectedWords is null ||
            PdfMarkupCanvas.ActualWidth <= 0 || PdfMarkupCanvas.ActualHeight <= 0)
            return;
        var point = e.GetPosition(PdfMarkupCanvas);
        int target = _selectedWords.HitTest(
            point.X / PdfMarkupCanvas.ActualWidth, point.Y / PdfMarkupCanvas.ActualHeight);
        if (target < 0 || target == _selectionEnd) return;
        _selectionEnd = target;
        RenderTextSelection();
        e.Handled = true;
    }

    private void FinishTextSelection(MouseButtonEventArgs e)
    {
        UpdateTextSelection(e);
        _textSelectionDragging = false;
        PdfMarkupCanvas.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void RenderTextSelection()
    {
        PdfTextSelectionCanvas.Children.Clear();
        CopySelectedTextButton.IsEnabled = false;
        if (!_textSelectionMode || _selectedWords is null ||
            _selectionAnchor < 0 || _selectionEnd < 0) return;
        string text = _selectedWords.Extract(_selectionAnchor, _selectionEnd);
        if (string.IsNullOrEmpty(text))
        {
            StatusText.Text = "Выделение превышает предел 50 000 символов.";
            return;
        }
        double width = PdfTextSelectionCanvas.Width;
        double height = PdfTextSelectionCanvas.Height;
        if (width <= 0 || height <= 0) return;
        for (int i = Math.Min(_selectionAnchor, _selectionEnd);
             i <= Math.Max(_selectionAnchor, _selectionEnd); i++)
        {
            var b = _selectedWords.Words[i].Bounds;
            var rectangle = new Rectangle
            {
                Width = b.Width * width, Height = b.Height * height,
                Fill = new SolidColorBrush(Color.FromArgb(100, 0, 130, 190)),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(rectangle, b.X * width);
            Canvas.SetTop(rectangle, b.Y * height);
            PdfTextSelectionCanvas.Children.Add(rectangle);
        }
        CopySelectedTextButton.IsEnabled = true;
    }

    private void CopySelectedText_Click(object sender, RoutedEventArgs e)
    {
        if (!_textSelectionMode || _selectedWords is null) return;
        string text = _selectedWords.Extract(_selectionAnchor, _selectionEnd);
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            StatusText.Text = $"Скопировано символов: {text.Length}. PDF не изменён.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or
                                   InvalidOperationException)
        {
            DiagnosticLog.Failure("pdf-selection-clipboard", ex);
            StatusText.Text = "Буфер обмена недоступен. Попробуйте ещё раз.";
        }
    }
}
