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
        else
        {
            var session = PdfTabSessionStore.Load();
            paths = session.Paths.Where(path => PdfOpenRequest.TryParse([path], out _)).ToArray();
            activeIndex = session.ActiveIndex;
        }
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
