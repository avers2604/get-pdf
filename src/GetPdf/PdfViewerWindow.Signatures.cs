using System.Text;
using System.Windows;

namespace GetPdf;

public partial class PdfViewerWindow
{
    private async void VerifyPdfSignatures_Click(object sender, RoutedEventArgs e)
    {
        if (_originalSourceCopyPath is null || _loading || _closing) return;
        string source = _originalSourceCopyPath;
        IReadOnlyList<PdfSignatureCheck> signatures;
        try
        {
            StatusText.Text = "Проверяем подписи и доступную локальную цепочку сертификатов…";
            signatures = await Task.Run(() => PdfSignatureInspector.VerifyOffline(source));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Failure("pdf-signature-check", ex);
            MessageBox.Show(HostWindow,
                "Не удалось выполнить автономную криптографическую проверку. " +
                "Подпись НЕ считается проверенной. Для работы с КЭП обратитесь в ИТ-службу.",
                "Проверка электронных подписей", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
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
        MessageBox.Show(HostWindow, text.ToString(), "Электронные подписи PDF",
            MessageBoxButton.OK, signatures.Any(s => s.State == PdfSignatureTrust.Failed)
                ? MessageBoxImage.Warning : MessageBoxImage.Information);
        StatusText.Text = signatures.Count == 0
            ? "Подписи не обнаружены."
            : $"Проверено локально подписей: {signatures.Count}. Полная проверка КЭП не выполнялась.";
    }
}
