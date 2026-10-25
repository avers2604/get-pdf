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
            string output = await RunAsync("lpstat", ["-e"], null, ct);
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

    public static async Task<string> SubmitPdfAsync(
        string pdfPath, PrintJobPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(pdfPath))
            throw new FileNotFoundException("PDF больше не доступен.");
        if (!string.Equals(Path.GetExtension(pdfPath), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Печатать можно только сохранённый PDF.");

        // Immediately re-check queue existence, not only the stale dropdown.
        var discovered = await PrintersAsync(ct);
        if (!discovered.Contains(plan.PrinterName, StringComparer.Ordinal))
            throw new InvalidOperationException("Принтер не найден. Обновите список устройств.");

        if (OperatingSystem.IsLinux())
        {
            var args = CupsPrintArguments.Create(plan, Path.GetFullPath(pdfPath));
            await RunAsync("lp", args, null, ct);
            return "Задание передано в очередь CUPS. Проверьте состояние принтера.";
        }
        if (OperatingSystem.IsWindows())
        {
            var request = new PrintWorkerRequest(
                Path.GetFullPath(pdfPath), plan.PrinterName, plan.SourcePageCount,
                plan.CupsPageRange, plan.Copies, plan.Duplex, plan.Paper);
            await RunAsync(WindowsWorker(), ["print"],
                JsonSerializer.Serialize(request), ct);
            return "Задание передано в очередь Windows. Проверьте состояние принтера.";
        }
        throw new PlatformNotSupportedException();
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
        string? stdin, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(stdin is null ? TimeSpan.FromSeconds(20) : TimeSpan.FromMinutes(5));
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            CreateNoWindow = true
        };
        foreach (string arg in argv) start.ArgumentList.Add(arg);
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

    private sealed record PrintWorkerRequest(string PdfPath, string PrinterName,
        int SourcePageCount, string PageRange, int Copies, PrintDuplex Duplex, PrintPaper Paper);
}
