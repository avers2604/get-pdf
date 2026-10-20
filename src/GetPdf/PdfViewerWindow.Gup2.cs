using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace GetPdf;

/// <summary>GUP-2 browser-only handoff; no PDF paths, cookies or JWT are transmitted.</summary>
internal static class Gup2PortalLink
{
    internal static Uri Validate(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 2048 ||
            candidate.Any(char.IsControl) ||
            !Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException(
                "Адрес GUP-2 должен быть HTTPS без логина, пароля, параметров и фрагментов.");
        return uri;
    }

    internal static Uri? FromPolicy(WorkstationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.AllowGup2Portal ? Validate(policy.Gup2PortalUrl) : null;
    }

    internal static void Open(WorkstationPolicy policy)
    {
        var uri = FromPolicy(policy) ??
            throw new InvalidOperationException("Переход в GUP-2 запрещён политикой.");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true
        });
    }
}

public partial class PdfViewerWindow
{
    private void Gup2Portal_Click(object sender, RoutedEventArgs e)
    {
        try { Gup2PortalLink.Open(WorkstationPolicy.Current); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                                   or System.IO.IOException)
        {
            DiagnosticLog.Failure("gup2-browser-handoff", ex);
            MessageBox.Show(HostWindow,
                "Не удалось открыть GUP-2. Проверьте HTTPS-адрес корпоративного " +
                "портала и доступность браузера.",
                "GUP-2", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
