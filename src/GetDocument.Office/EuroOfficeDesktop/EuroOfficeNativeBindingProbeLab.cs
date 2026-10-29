using System.Diagnostics;
using System.Text;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// Executes only the explicitly approved synthetic EuroOffice Qt/CEF binding
/// probe. No normal launcher uses this host; it never opens corporate files,
/// enables save, prints, accesses clipboard or grants document capabilities.
/// </summary>
public static class EuroOfficeNativeBindingProbeLab
{
    public static ProcessStartInfo CreateStartInfo(
        EuroOfficeNativeLabHostPolicy host,
        string stagingDirectory, string fixtureName,
        Guid sessionId, Guid tabId, bool labApproved)
    {
        // Reuse pinned executable SHA, local synthetic manifest, path and
        // Linux guards from the existing OSR laboratory process policy.
        ProcessStartInfo info = EuroOfficeOsrStreamProcessLab.CreateStartInfo(
            host, stagingDirectory, fixtureName, sessionId, tabId, labApproved);
        info.ArgumentList[0] = "--open-staged-binding-lab";
        return info;
    }

    public static async Task<EuroOfficeNativeBindingReceipt> RunAsync(
        EuroOfficeNativeLabHostPolicy host,
        string stagingDirectory, string fixtureName,
        Guid sessionId, Guid tabId, bool labApproved,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo info = CreateStartInfo(host, stagingDirectory,
            fixtureName, sessionId, tabId, labApproved);
        string json = await RunRawLabAsync(info, cancellationToken);
        return EuroOfficeNativeBindingReceipt.Parse(json, sessionId, tabId, fixtureName);
    }

    /// <summary>Shared strict local process lifecycle for the two synthetic lab receipt types.</summary>
    internal static async Task<string> RunRawLabAsync(ProcessStartInfo info,
        CancellationToken cancellationToken, int maxReceiptChars = 2048)
    {
        ArgumentNullException.ThrowIfNull(info);
        RequireBoundedReceiptLimit(maxReceiptChars);
        using var process = new Process { StartInfo = info };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        bool started = false;
        try
        {
            started = process.Start();
            if (!started)
                throw new IOException("EuroOffice Qt binding-стенд не запустился.");
            string json = await ReadOnlyReceiptAsync(
                process.StandardOutput, deadline.Token, maxReceiptChars);
            await process.WaitForExitAsync(deadline.Token);
            if (process.ExitCode != 0)
                throw new InvalidDataException("EuroOffice Qt binding-стенд завершился с ошибкой.");
            return json;
        }
        finally
        {
            if (started && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or
                    System.ComponentModel.Win32Exception) { }
                // Reap in the bounded lab after interrupting the process.
                using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(reap.Token); }
                catch (Exception ex) when (ex is InvalidOperationException or
                    OperationCanceledException) { }
            }
        }
    }

    /// <summary>
    /// Fail closed on additional output/lines, empty receipt, unbounded output,
    /// binary characters or truncated JSON; no stdout logs or echoed document
    /// paths are accepted as protocol. The Qt process may require sandboxing
    /// despite this bounded transport.
    /// </summary>
    internal static async Task<string> ReadOnlyReceiptAsync(
        TextReader reader, CancellationToken cancellationToken, int maxChars = 2048)
    {
        ArgumentNullException.ThrowIfNull(reader);
        RequireBoundedReceiptLimit(maxChars);
        var buffer = new char[1];
        var receipt = new StringBuilder();
        bool ended = false;
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken) > 0)
        {
            char ch = buffer[0];
            if (ch == '\n')
            {
                if (ended || receipt.Length == 0)
                    throw new InvalidDataException("Повторная или пустая квитанция CEF.");
                ended = true;
                continue;
            }
            if (ended || receipt.Length >= maxChars ||
                ch is '\r' or '\0' || char.IsControl(ch))
                throw new InvalidDataException("CEF отправил недопустимый диагностический вывод.");
            receipt.Append(ch);
        }
        if (!ended)
            throw new InvalidDataException("CEF завершился без полной binding-квитанции.");
        return receipt.ToString();
    }

    /// <summary>
    /// Only the two protocol-specific ceilings are permitted: historical
    /// binding v1 max 2048 and nested SaveAs v2 max 4096 characters.
    /// Unknown callers cannot silently disable the stdout size gate.
    /// </summary>
    private static void RequireBoundedReceiptLimit(int maxChars)
    {
        if (maxChars is not (2048 or 4096))
            throw new ArgumentOutOfRangeException(nameof(maxChars),
                "Only explicit v1/v2 synthetic receipt bounds are permitted.");
    }
}
