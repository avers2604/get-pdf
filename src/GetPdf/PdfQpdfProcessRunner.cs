using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace GetPdf;

/// <summary>
/// Single, bounded process boundary for the approved offline qpdf binary.
/// No shell, no network, no unbounded stdout/stderr retention. Every invocation
/// has a finite deadline, and exceptions or cancellations terminate the child
/// process tree before temporary files may be cleaned up.
/// </summary>
internal static class PdfQpdfProcessRunner
{
    internal const int MaxCapturedCharacters = 256;

    internal static TimeSpan TimeoutFor(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0)
            throw new ArgumentException("Нет команды qpdf.", nameof(args));
        return args[0] switch
        {
            "--show-npages" => TimeSpan.FromMinutes(2),
            "--check" => TimeSpan.FromMinutes(5),
            "--empty" => TimeSpan.FromMinutes(10),
            _ => TimeSpan.FromMinutes(5)
        };
    }

    internal static async Task<string> ReadOutputAsync(
        StreamReader reader, bool capture, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var result = capture ? new StringBuilder() : null;
        var buffer = new char[1024];
        bool tooLong = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation)) > 0)
        {
            if (!capture) continue;
            if (result!.Length + count > MaxCapturedCharacters)
            {
                tooLong = true;
                continue;
            }
            result.Append(buffer, 0, count);
        }
        if (tooLong)
            throw new InvalidDataException(
                "qpdf вернул слишком длинный ответ; результат не принимается.");
        return result?.ToString() ?? string.Empty;
    }

    public static async Task<(int ExitCode, string StandardOut)> RunAsync(
        IReadOnlyList<string> args, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var deadline = TimeoutFor(args);
        // Recheck IT-approved SHA-256 immediately before each native spawn.
        PdfQpdfValidator.EnsureApproved();
        var info = new ProcessStartInfo
        {
            FileName = PdfQpdfValidator.ExecutablePath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = info };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        linked.CancelAfter(deadline);
        try
        {
            if (!process.Start())
                throw new IOException("Не удалось запустить утверждённую утилиту qpdf.");

            // Both pipes are drained concurrently to avoid pipe-buffer deadlocks.
            // Capture only short --show-npages responses; discard diagnostics
            // because PDF paths may contain confidential document names.
            var stdout = ReadOutputAsync(process.StandardOutput,
                capture: args[0] == "--show-npages", linked.Token);
            var stderr = ReadOutputAsync(process.StandardError,
                capture: false, linked.Token);
            await Task.WhenAll(process.WaitForExitAsync(linked.Token), stdout, stderr);
            cancellation.ThrowIfCancellationRequested();
            return (process.ExitCode, await stdout);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // The process may have already exited. No native diagnostic
                // arguments or file paths are exposed to the end user.
            }

            // Avoid deleting temporary files while a cancelled qpdf still has
            // them open, but also avoid waiting without a finite bound.
            try
            {
                if (process.Id != 0)
                {
                    using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(grace.Token);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
            {
                // The calling job still treats this process as failed.
            }

            if (!cancellation.IsCancellationRequested && linked.IsCancellationRequested)
                throw new TimeoutException(
                    "Превышено допустимое время работы qpdf. " +
                    "Операция прервана; итоговый PDF не публикуется.");
            throw;
        }
    }
}
