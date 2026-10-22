using Microsoft.Win32;
using System.Windows;

namespace GetPdf;

public partial class PdfViewerWindow
{
    private async void FillTextForm_Click(object sender, RoutedEventArgs e)
    {
        if (_sourcePath is null || _loading || _closing || _rendering) return;
        if (HasPendingChanges)
        {
            GetMessageBox.Show(HostWindow, "Сначала сохраните текущие правки отдельно " +
                "или отмените изменения: заполнение формы работает только с неизменённым PDF.",
                "Заполнение формы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Hold the document open through preflight, modal input and publishing.
        // Closing a tab or replacing its source while the form is running
        // would otherwise make an asynchronous copy unsafe.
        string input = _originalSourceCopyPath ?? _sourcePath;
        string? initialPath = _sourcePath;
        using var cancellation = new CancellationTokenSource();
        _batchCancel = cancellation;
        _loading = true;
        SetControls();
        string? completedFile = null;
        try
        {
            IReadOnlyList<string> fields = await Task.Run(
                () => PdfTextFormService.ReadEditableFieldNames(input),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closing || !string.Equals(initialPath, _sourcePath,
                    StringComparison.OrdinalIgnoreCase))
                throw new OperationCanceledException();

            var editor = new PdfTextFormDialog(fields) { Owner = HostWindow };
            if (editor.ShowDialog() != true) return;
            var output = NewPdfOutputDialog(
                Path.GetFileNameWithoutExtension(_originalPath) + "_заполненный");
            output.Title = "Сохранить заполненную форму в новый PDF";
            if (output.ShowDialog(HostWindow) != true ||
                !ValidateOutput(output.FileName)) return;

            if (HasPendingChanges || !string.Equals(initialPath, _sourcePath,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Исходный PDF изменился.");
            cancellation.Token.ThrowIfCancellationRequested();

            await PdfJobCoordinator.Shared.RunAsync(async token =>
            {
                await Task.Run(() =>
                    PdfTextFormService.FillNewCopy(input, output.FileName,
                        editor.Values, token), token);
                return true;
            }, cancellation.Token);
            completedFile = output.FileName;
            StatusText.Text = "Форма записана в отдельный PDF с визуальным представлением полей.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Заполнение формы отменено. Исходный PDF не изменён.";
        }
        catch (NotSupportedException ex)
        {
            // Not a failure: the document simply is not a fillable form (or uses unsupported fields).
            StatusText.Text = "В этом PDF нет поддерживаемых полей формы.";
            GetMessageBox.Show(HostWindow, ex.Message + "\n\n" +
                "Команда работает с PDF-бланками, в которых есть поля ввода (AcroForm) — например, " +
                "формы из Acrobat или Word с полями. Для обычного документа используйте «Заметку» " +
                "или «Штамп» на вкладке «Разметка».",
                "Заполнение формы", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Failure("pdf-fill-form", ex);
            GetMessageBox.Show(HostWindow, "Не удалось безопасно сохранить форму. " +
                "Возможно, у неё неподдерживаемая структура либо не удалось " +
                "пересоздать визуальное представление текста. Исходный PDF не изменён.",
                "Заполнение формы", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _batchCancel = null;
            _loading = false;
            SetControls();
        }
        if (completedFile is not null && !_closing && HostWindow is PdfTabbedWindow shell)
            await shell.OpenPathsAsync([completedFile]);
    }
}
