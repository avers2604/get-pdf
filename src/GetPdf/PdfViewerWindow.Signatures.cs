using System.Text;
using System.Windows;

namespace GetPdf;

public partial class PdfViewerWindow
{
    private async void VerifyPdfSignatures_Click(object sender, RoutedEventArgs e)
    {
        if (_originalSourceCopyPath is null || _loading || _closing || _rendering) return;
        string source = _originalSourceCopyPath;
        IReadOnlyList<PdfSignatureCheck>? signatures = null;

        // A signature read is potentially expensive. Reserve the originating
        // tab so it cannot be disposed while the offline cryptography is
        // working and let the existing Cancel/Esc action signal cancellation.
        using var cancellation = new CancellationTokenSource();
        _batchCancel = cancellation;
        _loading = true;
        SetControls();
        try
        {
            StatusText.Text = "Проверяем подписи и локальную цепочку сертификатов…";
            signatures = await PdfJobCoordinator.Shared.RunAsync(async token =>
                await Task.Run(() =>
                    PdfSignatureInspector.VerifyOffline(source, token), token),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Проверка подписей отменена. Статус подписи не подтверждён.";
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Failure("pdf-signature-check", ex);
            GetMessageBox.Show(HostWindow,
                "Не удалось выполнить автономную криптографическую проверку. " +
                "Подпись НЕ считается проверенной. Для работы с КЭП обратитесь в ИТ-службу.",
                "Проверка электронных подписей", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            _batchCancel = null;
            _loading = false;
            SetControls();
        }
        if (signatures is null || _closing) return;
        var text = new StringBuilder();
        if (signatures.Count == 0)
        {
            text.AppendLine("Активных цифровых подписей не обнаружено.");
            text.AppendLine("Это не исключает пустые поля подписи или неподдерживаемую структуру PDF.");
        }
        else
        {
            text.AppendLine($"Обнаружено подписей: {signatures.Count}");
            text.AppendLine("Проверка выполнена автономно, без сетевых запросов.");
            text.AppendLine();
            foreach (var signature in signatures)
            {
                text.AppendLine(signature.ToRussianSummary());
                text.AppendLine();
            }
        }
        text.AppendLine("Не является подтверждением юридической действительности КЭП, " +
            "доверенной метки времени либо онлайн-статуса отзыва сертификатов.");
        GetMessageBox.Show(HostWindow, text.ToString(), "Электронные подписи PDF",
            MessageBoxButton.OK, signatures.Any(s => s.State == PdfSignatureTrust.Failed)
                ? MessageBoxImage.Warning : MessageBoxImage.Information);
        StatusText.Text = signatures.Count == 0
            ? "Подписи не обнаружены."
            : $"Проверено локально подписей: {signatures.Count}. Полная проверка КЭП не выполнялась.";
    }
}
