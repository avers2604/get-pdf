using System.Diagnostics;

namespace GetDocument.Desktop.Integration;

/// <summary>
/// "ГЭТ Сканирование" is a separate application (repository GUP-Scan, MSI installs
/// %ProgramFiles%\GUP-Scan\GupScan.exe). GET Document only starts it: scanner drivers,
/// TWAIN/WIA and the scan engine are never loaded into this process.
/// </summary>
internal static class ScannerApp
{
    private static IEnumerable<string> Candidates()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                 }.Where(r => !string.IsNullOrEmpty(r)))
        {
            yield return Path.Combine(root, "GUP-Scan", "GupScan.exe");
            yield return Path.Combine(root, "GET Scan", "GupScan.exe");
        }
    }

    public static string? Executable =>
        OperatingSystem.IsWindows() ? Candidates().FirstOrDefault(File.Exists) : null;

    public static bool IsInstalled => Executable is not null;

    public static bool TryLaunch(out string message)
    {
        if (Executable is not { } exe)
        {
            message = "Приложение «ГЭТ Сканирование» не установлено на этом компьютере. " +
                      "Попросите ИТ-службу установить его — после этого кнопка запустит сканирование. " +
                      "Готовые PDF из сканера открываются здесь обычным способом.";
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
            message = "Запущено «ГЭТ Сканирование». Сохранённый PDF можно открыть здесь.";
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            message = "Не удалось запустить «ГЭТ Сканирование»: " + ex.Message;
            return false;
        }
    }
}
