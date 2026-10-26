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
            GetMessageBox.Show(HostWindow,
                "Сначала сохраните изменения в отдельный файл и откройте его. " +
                "Оптимизация не должна терять несохранённые операции.",
                "Оптимизация PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // No "Проверка PDF" prompt: qpdf rewrites losslessly (bookmarks, links and text stay), and
        // PdfLosslessOptimizer itself refuses signed PDFs, forms and annotations via PdfStructureGuard.
        var dialog = NewPdfOutputDialog(Path.GetFileNameWithoutExtension(_originalPath!) + "_сжатый");
        if (dialog.ShowDialog(HostWindow) != true || !ValidateOutput(dialog.FileName)) return;
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
            StatusText.Text = DescribeCompression(result.Before, result.After, Path.GetFileName(target));
        }
        catch (OperationCanceledException)
        { StatusText.Text = "Оптимизация отменена; новая копия не опубликована."; }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("pdf-optimize", ex);
            StatusText.Text = "Оптимизация не завершена. Исходный PDF не изменён.";
            GetMessageBox.Show(HostWindow, ex.Message, "Оптимизация PDF",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _batchCancel = null; _loading = false; SetControls(); }
    }

    /// <summary>"Сжатая копия «a.pdf»: 167 КБ → 71 КБ (−57 %). Исходный файл не изменён."</summary>
    internal static string DescribeCompression(long before, long after, string fileName)
    {
        static string Size(long bytes) => bytes >= 1024 * 1024
            ? $"{bytes / (1024.0 * 1024):0.#} МБ"
            : $"{Math.Max(1, (bytes + 512) / 1024)} КБ";
        int saved = before > 0 ? (int)Math.Round(100.0 * (before - after) / before) : 0;
        return $"Сжатая копия «{fileName}»: {Size(before)} → {Size(after)} (−{saved} %). Исходный файл не изменён.";
    }
}
