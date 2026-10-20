using System.Windows;
namespace GetPdf;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        try { DiagnosticLog.Initialize(); }
        catch (Exception) { /* Diagnostics are optional. */ }
        base.OnStartup(e);
        try { _ = WorkstationPolicy.Current; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Failure("pdf-policy", ex);
            MessageBox.Show("Ошибка корпоративной политики ГЭТ PDF. Обратитесь в ИТ-службу.",
                "ГЭТ PDF", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(3);
            return;
        }
        if (!WorkstationPolicy.Current.AllowPdfViewer)
        {
            MessageBox.Show("Просмотр PDF запрещён политикой ИТ-службы.", "ГЭТ PDF");
            Shutdown(3);
            return;
        }
        // Disabled corporate history options purge old user-local path metadata.
        try
        {
            if (!WorkstationPolicy.Current.RememberRecentDocuments)
                PdfRecentDocuments.Clear();
            if (!WorkstationPolicy.Current.RestoreTabs)
                PdfTabSessionStore.Clear();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { DiagnosticLog.Failure("pdf-privacy-cleanup", ex); }

        string[] paths;
        int activeIndex = 0;
        if (e.Args.Length > 0)
        {
            if (e.Args.Length > 16 || e.Args.Any(path =>
                !PdfOpenRequest.TryParse([path], out _)))
            {
                MessageBox.Show("Укажите от 1 до 16 существующих PDF-файлов.", "ГЭТ PDF");
                Shutdown(2);
                return;
            }
            paths = e.Args.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        else if (WorkstationPolicy.Current.RestoreTabs)
        {
            var session = PdfTabSessionStore.Load();
            paths = session.Paths.Where(path => PdfOpenRequest.TryParse([path], out _)).ToArray();
            activeIndex = session.ActiveIndex;
        }
        else paths = [];
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        var shell = new PdfTabbedWindow(paths, activeIndex);
        MainWindow = shell;
        shell.Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        DiagnosticLog.Close();
        base.OnExit(e);
    }
}
