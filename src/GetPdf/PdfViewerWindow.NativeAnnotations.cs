using System.Windows;

namespace GetPdf;

public partial class PdfViewerWindow
{
    private async void NativeDraftSave_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _rendering || _closing || _plan is null ||
            _sourcePath is null || _originalSourceCopyPath is null || !_canModify)
            return;
        var drafts = _nativeDrafts.Snapshot();
        if (drafts.Count == 0) return;
        if (drafts.Any(d => d.Kind != "text"))
        {
            MessageBox.Show(HostWindow,
                "Настоящие PDF-аннотации пока поддерживаются только для текстовых комментариев. " +
                "Рамки и выделение остаются эскизами; удалите или отмените их перед сохранением.",
                "Сохранение PDF-аннотаций", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // A draft references the original page indexes. A reordered or replaced
        // page plan cannot safely be annotated with those stale coordinates.
        if (_plan.HasChanges || !string.Equals(_sourcePath, _originalSourceCopyPath,
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(HostWindow,
                "Сначала сохраните или отмените изменения страниц. " +
                "Интерактивные комментарии можно добавить только к неизменённому составу PDF.",
                "Сохранение PDF-аннотаций", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string source = _originalSourceCopyPath;
        var dialog = NewPdfOutputDialog(
            Path.GetFileNameWithoutExtension(_originalPath) + "_комментарии");
        dialog.Title = "Сохранить интерактивные текстовые комментарии в отдельный PDF";
        if (dialog.ShowDialog(HostWindow) != true || !ValidateOutput(dialog.FileName))
            return;

        string output = dialog.FileName;
        bool completed = false;
        _loading = true;
        SetControls();
        try
        {
            await RunPdfFileJobAsync(token => Task.Run(() =>
                PdfNativeTextAnnotationWriter.SaveNewCopy(source, output, drafts, token),
                token));
            completed = true;
            _nativeDrafts.Discard();
            _nativeDraftMode = null;
            if (_plan is not null) ShowRectangles(_plan.Pages[_pageIndex]);
            StatusText.Text = $"Сохранено текстовых PDF-аннотаций: {drafts.Count}. " +
                "Исходный PDF не изменён.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Запись PDF-комментариев отменена, исходник не изменён.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Failure("pdf-native-comment-export", ex);
            MessageBox.Show(HostWindow,
                "Не удалось проверить или сохранить текстовые аннотации PDF. " +
                "Оригинал не изменён. Выберите другой простой PDF без форм, " +
                "подписей, действий и существующих аннотаций.",
                "Сохранение PDF-аннотаций", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _loading = false;
            SetControls();
        }
        if (completed && !_closing && HostWindow is PdfTabbedWindow shell)
            await shell.OpenPathsAsync([output]);
    }
}
