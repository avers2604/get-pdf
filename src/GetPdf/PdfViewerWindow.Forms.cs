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
            MessageBox.Show(HostWindow, "Сначала сохраните текущие правки отдельно " +
                "или отмените изменения: заполнение формы работает только с неизменённым PDF.",
                "Заполнение формы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string input = _originalSourceCopyPath ?? _sourcePath;
        IReadOnlyList<string> fields;
        try
        {
            fields = await Task.Run(() => PdfTextFormService.ReadEditableFieldNames(input));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Failure("pdf-form-preflight", ex);
            MessageBox.Show(HostWindow, "Эта форма не поддерживается безопасным режимом " +
                "заполнения. Доступны только простые неподписанные текстовые поля " +
                "без сценариев, XFA и других интерактивных элементов.",
                "Заполнение формы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var editor = new PdfTextFormDialog(fields) { Owner = HostWindow };
        if (editor.ShowDialog() != true) return;
        var output = NewPdfOutputDialog(
            Path.GetFileNameWithoutExtension(_originalPath) + "_заполненный");
        output.Title = "Сохранить заполненную форму в новый PDF";
        if (output.ShowDialog(HostWindow) != true ||
            !ValidateOutput(output.FileName)) return;

        bool completed = false;
        _loading = true; SetControls();
        try
        {
            await PdfJobCoordinator.Shared.RunAsync(async token =>
            {
                await Task.Run(() =>
                    PdfTextFormService.FillNewCopy(input, output.FileName,
                        editor.Values, token), token);
                return true;
            }, CancellationToken.None);
            completed = true;
            StatusText.Text = "Заполненная форма записана в отдельный PDF. Исходник не изменён.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Failure("pdf-fill-form", ex);
            MessageBox.Show(HostWindow, "Не удалось подтвердить сохранение формы. " +
                "Исходный PDF не изменён; выходной файл не опубликован.",
                "Заполнение формы", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _loading = false; SetControls(); }
        if (completed && HostWindow is PdfTabbedWindow shell)
            await shell.OpenPathsAsync([output.FileName]);
    }
}
