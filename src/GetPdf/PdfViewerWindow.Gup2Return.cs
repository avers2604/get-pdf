using System.Windows;

namespace GetPdf;

public partial class PdfViewerWindow
{
    private async void Gup2DirectReturn_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || IsBusy || _sourcePath is null ||
            !WorkstationPolicy.Current.AllowGup2DirectReturn)
            return;
        var dialog = new Gup2DirectReturnDialog { Owner = HostWindow };
        if (dialog.ShowDialog() != true)
            return;

        string target = Path.GetFullPath(dialog.PdfPath);
        if (string.Equals(target, _originalPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, _originalSourceCopyPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, _sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            GetMessageBox.Show(HostWindow,
                "Выберите отдельную сохранённую PDF-копию, а не исходный документ.",
                "Возврат в GUP-2", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string oneTimeCode = dialog.OneTimeCode;
        using var cancellation = new CancellationTokenSource();
        _batchCancel = cancellation;
        _loading = true;
        SetControls();
        try
        {
            StatusText.Text = "Защищённая отправка новой PDF-копии в GUP-2…";
            await PdfJobCoordinator.Shared.RunAsync(async token =>
            {
                await Gup2DesktopReturnClient.UploadAsync(
                    target, oneTimeCode, WorkstationPolicy.Current, token);
                return true;
            }, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            StatusText.Text = "GUP-2 принял копию на обработку. Проверьте WORM-статус в карточке.";
            GetMessageBox.Show(HostWindow,
                "PDF-копия принята сервером GUP-2 на обработку. " +
                "Это НЕ подтверждение переноса в WORM или юридической новой редакции. " +
                "Откройте карточку документа в браузере и проверьте результат.",
                "Возврат PDF", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Запрошена отмена загрузки. Проверьте карточку GUP-2: " +
                "сервер мог принять файл до отмены.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Failure("gup2-direct-return", ex);
            StatusText.Text = "Возврат копии не подтверждён сервером GUP-2.";
            GetMessageBox.Show(HostWindow,
                "GUP-2 не подтвердил приём PDF. Код мог истечь или быть использован, " +
                "либо изменились права, WORM-оригинал или версия карточки. " +
                "Проверьте документ и получите новый одноразовый код.",
                "Возврат PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _batchCancel = null;
            _loading = false;
            SetControls();
        }
    }
}
