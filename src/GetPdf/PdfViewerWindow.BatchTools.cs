using System.Windows;
using Microsoft.Win32;

namespace GetPdf;

public partial class PdfViewerWindow
{
    private void NumberAllPages_Click(object sender, RoutedEventArgs e)
    {
        if (!_workspace.CanEdit || _plan is null) return;
        int count = _plan.Pages.Count;
        bool existing = _plan.Pages.Any(p => p.Stamp.Length > 0);
        string question = existing
            ? "На некоторых страницах уже есть штампы. Заменить их нумерацией всех страниц?"
            : "Добавить номера на все страницы? Их можно отменить (Ctrl+Z).";
        if (MessageBox.Show(this, question, "Нумерация PDF",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ApplyEdit(p =>
        {
            for (int i = 0; i < count; i++)
                p.SetStamp(i, PdfBatchPageLabels.Number(i + 1, count));
        });
        StatusText.Text = "Номера добавлены к черновику; сохраните отдельную копию PDF.";
    }

    private async void CopyPageText_Click(object sender, RoutedEventArgs e)
    {
        if (_sourcePath is null || _plan is null || _loading) return;
        try
        {
            string source = _sourcePath;
            int index = _plan.Pages[_pageIndex].SourcePage;
            _textIndex ??= await Task.Run(() => PdfTextReader.Build(source));
            string text = _textIndex.GetSourceText(index);
            if (string.IsNullOrWhiteSpace(text))
            {
                StatusText.Text = "Текстового слоя нет. Для изображения используйте OCR.";
                return;
            }
            if (text.Length > 200_000)
            {
                StatusText.Text = "Страница слишком велика для копирования в буфер обмена.";
                return;
            }
            Clipboard.SetText(text);
            StatusText.Text = "Текст страницы скопирован в буфер обмена.";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("pdf-copy-text", ex);
            StatusText.Text = "Не удалось скопировать текст страницы.";
        }
    }

    private async void SplitIntoPages_Click(object sender, RoutedEventArgs e)
    {
        if (!_workspace.CanExtract || _plan is null || _sourcePath is null || _originalPath is null) return;
        var pages = _plan.Pages.ToArray();
        if (pages.Length > PdfSplitPageNaming.MaxPerOperation)
        {
            MessageBox.Show(this, $"За один раз можно разделить не более {PdfSplitPageNaming.MaxPerOperation} страниц.",
                "Разделение PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!ConfirmStructuralExport()) return;
        var dialog = NewPdfOutputDialog(Path.GetFileNameWithoutExtension(_originalPath) + "_страница");
        dialog.Title = "Выберите папку и префикс имён для отдельных PDF";
        if (dialog.ShowDialog(this) != true) return;
        var outputs = PdfSplitPageNaming.Plan(dialog.FileName, pages.Length);
        if (outputs.Any(File.Exists))
        {
            MessageBox.Show(this, "Один или несколько файлов уже существуют. Укажите другое имя префикса.",
                "Разделение PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this, $"Создать {pages.Length} отдельных PDF? Исходный файл не меняется.",
            "Разделение PDF", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        using var cancel = new CancellationTokenSource();
        _batchCancel = cancel;
        _loading = true; SetControls();
        string input = _sourcePath;
        var completed = new List<string>();
        StatusText.Text = "Разделяем PDF на страницы…";
        try
        {
            await PdfJobCoordinator.Shared.RunAsync(async token =>
            {
                for (int i = 0; i < pages.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    int page = i;
                    await Task.Run(() => PdfDocumentWriter.SaveAs(input, outputs[page], [pages[page]], token), token);
                    completed.Add(outputs[page]);
                }
                return true;
            }, cancel.Token);
            StatusText.Text = $"Создано {completed.Count} отдельных PDF.";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("pdf-split", ex);
            foreach (string path in completed)
            {
                try { File.Delete(path); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                { DiagnosticLog.Failure("pdf-split-cleanup", cleanup); }
            }
            StatusText.Text = "Разделение не завершено. Созданные файлы по возможности удалены.";
            if (ex is not OperationCanceledException)
                MessageBox.Show(this, "Разделить документ не удалось. Проверьте свободное место и права на папку.",
                    "Разделение PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _batchCancel = null; _loading = false; SetControls(); }
    }
}
