using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GetDocument.Core.Printing;

namespace GetDocument.Desktop.Printing;

/// <summary>
/// GET-owned UI delegates printing to installed OS queues. No system print
/// dialog, powershell, shell interpreter, or cloud/network print service.
/// </summary>
internal static class LocalPrintBroker
{
    public static async Task<IReadOnlyList<string>> PrintersAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsLinux())
        {
            string output;
            try { output = await RunAsync("lpstat", ["-e"], null, ct); }
            catch (IOException)
            {
                // Older local CUPS installations may lack lpstat -e.
                output = await RunAsync("lpstat", ["-p"], null, ct);
                return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Where(x => x.StartsWith("printer ", StringComparison.Ordinal))
                    .Select(x => x["printer ".Length..].Split(' ', 2)[0])
                    .Where(ValidQueueName)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            }
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(ValidQueueName)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        if (OperatingSystem.IsWindows())
        {
            string output = await RunAsync(WindowsWorker(), ["printers"], null, ct);
            string[]? queues = JsonSerializer.Deserialize<string[]>(output);
            return (queues ?? []).Where(ValidQueueName)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        throw new PlatformNotSupportedException("Печать поддерживается только Windows и Linux.");
    }

    public static async Task<IReadOnlyList<PrintTrayOption>> TraysAsync(
        string printer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(printer) || printer.Length > 160 ||
            printer.StartsWith("-", StringComparison.Ordinal) || printer.Any(char.IsControl))
            throw new ArgumentException("Недопустимое имя принтера.", nameof(printer));
        ct.ThrowIfCancellationRequested();
        if (OperatingSystem.IsLinux())
        {
            string output = await RunAsync("lpoptions", ["-p", printer, "-l"], null, ct);
            return PrintTrayOptions.ParseCups(output);
        }
        if (OperatingSystem.IsWindows())
        {
            string json = await RunAsync(WindowsWorker(), ["trays", printer], null, ct);
            var trays = JsonSerializer.Deserialize<TrayReply[]>(json) ?? [];
            if (trays.Length > 32) return [];
            return trays.Where(t => PrintTrayOptions.IsValidValue(t.Value))
                .Select(t => PrintTrayOptions.Make("InputBin", t.Value, t.Label))
                .ToArray();
        }
        throw new PlatformNotSupportedException();
    }

    public static async Task<string> SubmitPdfAsync(
        string pdfPath, PrintJobPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ct.ThrowIfCancellationRequested();
        // Everything up to the actual lp / XpsDocumentWriter call is refused with
        // PrintNotSubmittedException: nothing is queued, so the user may simply retry.
        if (!File.Exists(pdfPath))
            throw new PrintNotSubmittedException("PDF больше не доступен. Задание не отправлено.");
        if (!string.Equals(Path.GetExtension(pdfPath), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new PrintNotSubmittedException("Печатать можно только сохранённый PDF.");

        // Immediately re-check queue existence, not only the stale dropdown.
        var discovered = await PrintersAsync(ct);
        if (!discovered.Contains(plan.PrinterName, StringComparer.Ordinal))
            throw new PrintNotSubmittedException("Принтер не найден. Задание не отправлено: обновите список устройств.");

        if (plan.Tray is { } requestedTray)
        {
            var currentlyAvailable = await TraysAsync(plan.PrinterName, ct);
            if (!PrintTrayOptions.IsSupported(currentlyAvailable, requestedTray))
                throw new InvalidOperationException(
                    "Выбранный лоток больше не поддерживается принтером. Обновите список лотков.");
        }
        if (OperatingSystem.IsLinux())
        {
            if (plan.Color != PrintColorMode.PrinterDefault)
            {
                // Do not silently ignore color overrides on older PPD/IPP drivers.
                string advertised = await RunAsync("lpoptions",
                    ["-p", plan.PrinterName, "-l"], null, ct);
                if (!CupsColorCapabilities.Supports(advertised, plan.Color))
                    throw new PrintNotSubmittedException(
                        $"Принтер «{plan.PrinterName}» не подтверждает выбранный цветовой режим. " +
                        "Задание не отправлено: выберите «Настройка принтера» или другое устройство.");
            }
            var args = CupsPrintArguments.Create(plan, Path.GetFullPath(pdfPath));
            await RunAsync("lp", args, null, ct);
            return "Задание передано в очередь CUPS. Проверьте состояние принтера.";
        }
        if (OperatingSystem.IsWindows())
        {
            var request = new PrintWorkerRequest(
                Path.GetFullPath(pdfPath), plan.PrinterName, plan.SourcePageCount,
                plan.CupsPageRange, plan.Copies, plan.Duplex, plan.Paper,
                plan.Orientation, plan.Color, plan.PagesPerSheet, plan.Tray);
            await RunAsync(WindowsWorker(), ["print"],
                JsonSerializer.Serialize(request), ct, plan.PrinterName);
            return "Задание передано в очередь Windows. Проверьте состояние принтера.";
        }
        throw new PlatformNotSupportedException();
    }

    /// <summary>The user's default printer (Windows setting / CUPS default), or null.</summary>
    public static async Task<string?> DefaultPrinterAsync(CancellationToken ct)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Windows");
                // "HP DeskJet 5000 series,winspool,Ne01:" — the queue name is before the first comma.
                return key?.GetValue("Device") is string device && device.Length > 0 ? device.Split(',')[0] : null;
            }
            if (OperatingSystem.IsLinux())
            {
                string output = await RunAsync("lpstat", ["-d"], null, ct);
                int colon = output.LastIndexOf(':');
                string name = colon >= 0 ? output[(colon + 1)..].Trim() : "";
                return ValidQueueName(name) ? name : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or
                                   System.ComponentModel.Win32Exception or OperationCanceledException) { }
        return null;
    }

    private static bool ValidQueueName(string? x) =>
        !string.IsNullOrWhiteSpace(x) && x.Length <= 160 &&
        x[0] != '-' && x.All(c => !char.IsControl(c));

    private static string WindowsWorker()
    {
        string location = Path.Combine(AppContext.BaseDirectory, "GetDocument.PrintWindows.exe");
        if (!File.Exists(location))
            throw new FileNotFoundException(
                "Не установлен служебный компонент печати для Windows.");
        return location;
    }

    private static async Task<string> RunAsync(string program, IReadOnlyList<string> argv,
        string? stdin, CancellationToken cancel, string? printWorkerQueue = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        bool isPrintJob = (argv.Count > 0 && (argv[0] == "-d" || argv[0] == "print"));
        timeout.CancelAfter(isPrintJob ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(20));
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            CreateNoWindow = true
        };
        foreach (string arg in argv) start.ArgumentList.Add(arg);
        if (OperatingSystem.IsLinux() && program == "lpstat")
            start.Environment["LC_ALL"] = "C"; // predictable fallback parsing across locales
        using var process = Process.Start(start) ??
            throw new IOException("Не удалось запустить локальную службу печати.");
        try
        {
            var stdOut = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stdErr = process.StandardError.ReadToEndAsync(timeout.Token);
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), timeout.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(timeout.Token);
            string output = await stdOut;
            _ = await stdErr; // Never surface arbitrary driver stderr/paths to end-user.
            if (printWorkerQueue is not null &&
                PrintRejection.Describe(process.ExitCode, output.Length <= 256 ? output : "", printWorkerQueue) is { } refused)
                throw new PrintNotSubmittedException(refused);
            if (process.ExitCode != 0)
                throw new IOException("Служба печати отклонила задание либо недоступна.");
            if (output.Length > 65536)
                throw new InvalidDataException("Некорректный ответ службы печати.");
            return output;
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private sealed record TrayReply(string Value, string Label);

    private sealed record PrintWorkerRequest(string PdfPath, string PrinterName,
        int SourcePageCount, string PageRange, int Copies, PrintDuplex Duplex, PrintPaper Paper,
        PrintOrientation Orientation, PrintColorMode Color, PrintPagesPerSheet PagesPerSheet,
        PrintTrayOption? Tray);
}
