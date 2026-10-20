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
        var paths = e.Args.Length == 0 ? new string?[] { null } : e.Args.Select(x => (string?)x).ToArray();
        var windows = new List<PdfViewerWindow>();
        foreach (string? path in paths)
        {
            if (path is not null && !PdfOpenRequest.TryParse([path], out _))
            {
                MessageBox.Show("Укажите существующий PDF-файл.", "ГЭТ PDF");
                Shutdown(2);
                return;
            }
            windows.Add(new PdfViewerWindow(path));
        }
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        foreach (var window in windows) window.Show();
        MainWindow = windows[0];
    }
    protected override void OnExit(ExitEventArgs e)
    {
        DiagnosticLog.Close();
        base.OnExit(e);
    }
}
