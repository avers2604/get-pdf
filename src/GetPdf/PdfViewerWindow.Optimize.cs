using Microsoft.Win32;
using System.Windows;

namespace GetPdf;

public partial class PdfViewerWindow
{
    private async void OptimizePdf_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _closing || !_canModify || _sourcePath is null || _plan is null) return;
        if (_plan.HasChanges)
        {
            MessageBox.Show(this,
                "Сначала сохраните изменения в отдельный файл и откройте его. " +
                "Оптимизация не должна терять несохранённые операции.",
                "Оптимизация PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!ConfirmStructuralExport()) return;
        var dialog = NewPdfOutputDialog(Path.GetFileNameWithoutExtension(_originalPath!) + "_оптимизированный");
        if (dialog.ShowDialog(this) != true || !ValidateOutput(dialog.FileName)) return;
        string source = _sourcePath;
        string target = dialog.FileName;
        int pages = _plan.Pages.Count;
        using var cancel = new CancellationTokenSource();
        _batchCancel = cancel;
        _loading = true; SetControls();
        StatusText.Text = "Проверяем и оптимизируем PDF без потерь качества изображений…";
        try
        {
            var result = await PdfJobCoordinator.Shared.RunAsync(token =>
                PdfLosslessOptimizer.OptimizeAsync(source, target, pages, token), cancel.Token);
            StatusText.Text = $"Создана отдельная копия: {result.Before - result.After:N0} байт экономии.";
        }
        catch (OperationCanceledException)
        { StatusText.Text = "Оптимизация отменена; новая копия не опубликована."; }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("pdf-optimize", ex);
            StatusText.Text = "Оптимизация не завершена. Исходный PDF не изменён.";
            MessageBox.Show(this, ex.Message, "Оптимизация PDF",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _batchCancel = null; _loading = false; SetControls(); }
    }
}
