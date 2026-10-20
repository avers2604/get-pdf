using System.Windows;

namespace GetPdf;

public partial class PdfViewerWindow
{
    private async void Gup2VerifySha_Click(object sender, RoutedEventArgs e)
    {
        if (!WorkstationPolicy.Current.AllowGup2Portal || _closing || IsBusy ||
            _originalSourceCopyPath is null)
            return;

        var dialog = new Gup2HashDialog { Owner = HostWindow };
        if (dialog.ShowDialog() != true) return;
        using var cancellation = new CancellationTokenSource();
        _batchCancel = cancellation;
        _loading = true;
        SetControls();
        try
        {
            bool equal = await Gup2PdfIntegrity.VerifyAsync(
                _originalSourceCopyPath, dialog.ExpectedSha256,
                WorkstationPolicy.Current.MaxPdfInputMiB * 1024L * 1024L,
                cancellation.Token);
            if (equal)
            {
                StatusText.Text = "SHA-256 совпал с указанным хешем GUP-2.";
                MessageBox.Show(HostWindow,
                    "SHA-256 загруженного оригинала совпадает с указанным хешем GUP-2. " +
                    "Это проверка байтовой целостности, а не прав доступа, подписи или юридической силы.",
                    "ГЭТ PDF ↔ GUP-2", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                StatusText.Text = "SHA-256 НЕ совпал. Возможен другой файл или другая редакция.";
                MessageBox.Show(HostWindow,
                    "SHA-256 НЕ СОВПАЛ. Не загружайте этот файл обратно в GUP-2 и " +
                    "сверьте оригинал с карточкой документа.",
                    "ГЭТ PDF ↔ GUP-2", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Проверка SHA-256 отменена.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Failure("gup2-pdf-hash-check", ex);
            MessageBox.Show(HostWindow,
                "Невозможно проверить локальный PDF: файл недоступен либо размер превышает лимит.",
                "ГЭТ PDF ↔ GUP-2", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _batchCancel = null;
            _loading = false;
            SetControls();
        }
    }
}
