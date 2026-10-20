using Microsoft.Win32;
using System.Windows;

namespace GetPdf;

public partial class PdfViewerWindow
{
    private async void ComparePdf_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _closing || _sourcePath is null || _plan is null) return;
        var dialog = new OpenFileDialog
        {
            Title = "Выберите второй PDF для сравнения текста",
            Filter = "PDF (*.pdf)|*.pdf"
        };
        if (dialog.ShowDialog(this) != true) return;
        string other = dialog.FileName;
        if (!PdfOpenRequest.TryParse([other], out _)) return;
        if (new FileInfo(other).Length > WorkstationPolicy.Current.MaxPdfInputMiB * 1024L * 1024L)
        {
            MessageBox.Show(this, "Второй PDF превышает установленный лимит.", "Сравнение PDF",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string first = _sourcePath;
        _loading = true;
        SetControls();
        StatusText.Text = "Сравниваем текстовый слой двух PDF…";
        try
        {
            var result = await Task.Run(() => PdfTextComparisonService.CompareFiles(first, other));
            StatusText.Text = "Сравнение текста завершено; изображения не проверялись.";
            MessageBox.Show(this, result.ToRussianSummary(), "Сравнение PDF по тексту",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("pdf-text-compare", ex);
            StatusText.Text = "Не удалось сравнить текстовые слои PDF.";
            MessageBox.Show(this, "Файл защищён, повреждён или не содержит доступной структуры для сравнения.",
                "Сравнение PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _loading = false; if (!_closing) SetControls(); }
    }
}