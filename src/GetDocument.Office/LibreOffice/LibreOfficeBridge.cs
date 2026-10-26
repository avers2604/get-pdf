using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// One warm headless LibreOffice per GET Document process, driven over UNO by
/// lo_bridge.py on LibreOffice's bundled Python. A cold LibreOffice start costs 10–30 s on
/// office PCs; through the bridge a preview takes seconds. Requests are serialized; any
/// failure tears the bridge down so the caller can fall back to a one-shot conversion.
/// </summary>
internal sealed class LibreOfficeBridge : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(150);
    private static readonly Lazy<LibreOfficeBridge> SharedInstance = new(() => new LibreOfficeBridge());
    public static LibreOfficeBridge Shared => SharedInstance.Value;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _office;
    private Process? _python;
    private bool _disabled;

    private LibreOfficeBridge()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    /// <summary>LibreOffice's own Python (with the uno module); null if absent.</summary>
    internal static string? PythonFor(string soffice)
    {
        string folder = Path.GetDirectoryName(soffice)!;
        if (OperatingSystem.IsWindows())
        {
            string bundled = Path.Combine(folder, "python.exe");
            return File.Exists(bundled) ? bundled : null;
        }
        // Linux distributions ship the uno module for the system interpreter (python3-uno).
        return File.Exists("/usr/bin/python3") ? "/usr/bin/python3" : null;
    }

    /// <summary>Starts LibreOffice in the background so the first preview is fast.</summary>
    public void Prewarm() => _ = Task.Run(async () =>
    {
        try { await EnsureStartedAsync(CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or Win32Exception) { }
    });

    /// <returns>False when the bridge is unavailable (caller falls back to one-shot conversion).</returns>
    public async Task<bool> TryConvertAsync(string input, string output, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_disabled) return false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!await EnsureStartedLockedAsync(cancellationToken)) return false;
            string request = JsonSerializer.Serialize(new { @in = input, @out = output });
            await _python!.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken);
            await _python.StandardInput.FlushAsync(cancellationToken);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            string? reply = await _python.StandardOutput.ReadLineAsync(linked.Token);
            if (reply is null) { Stop(); return false; }
            using var json = JsonDocument.Parse(reply);
            if (json.RootElement.GetProperty("ok").GetBoolean()) return File.Exists(output);
            string error = json.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
            if (error.Contains("disposed", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("NoConnect", StringComparison.OrdinalIgnoreCase))
            {
                Stop(); // LibreOffice went away: restart next time, one-shot conversion now
                return false;
            }
            throw new InvalidDataException("LibreOffice не смог открыть документ. Возможно, файл повреждён или защищён паролем.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Stop(); // hung on a document: restart for the next one
            throw new TimeoutException("LibreOffice не успел подготовить документ.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or Win32Exception or KeyNotFoundException)
        {
            Stop();
            return false;
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await EnsureStartedLockedAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task<bool> EnsureStartedLockedAsync(CancellationToken cancellationToken)
    {
        if (_python is { HasExited: false } && _office is { HasExited: false }) return true;
        Stop();
        string? soffice = LibreOfficeRuntime.Locate();
        string? python = soffice is null ? null : PythonFor(soffice);
        if (soffice is null || python is null) { _disabled = true; return false; }

        string pipe = $"getdoc-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}";
        var office = new ProcessStartInfo(soffice) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[]
                 {
                     "-env:UserInstallation=" + LibreOfficeRuntime.ConversionProfileUri(),
                     // No --nodefault here: with it LibreOffice quits after the last document closes,
                     // which would drop the bridge after every conversion.
                     "--headless", "--invisible", "--nologo", "--norestore", "--nolockcheck",
                     $"--accept=pipe,name={pipe};urp;StarOffice.ComponentContext"
                 })
            office.ArgumentList.Add(arg);
        _office = Process.Start(office) ?? throw new IOException("LibreOffice не запустился.");
        try { _office.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }

        var bridge = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        bridge.ArgumentList.Add(ScriptPath());
        bridge.ArgumentList.Add(pipe);
        bridge.ArgumentList.Add(((int)StartTimeout.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        bridge.Environment["PYTHONIOENCODING"] = "utf-8";
        _python = Process.Start(bridge) ?? throw new IOException("Мост LibreOffice не запустился.");
        _ = _python.StandardError.ReadToEndAsync(CancellationToken.None); // never let stderr block

        using var ready = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ready.CancelAfter(StartTimeout + TimeSpan.FromSeconds(10));
        string? hello = await _python.StandardOutput.ReadLineAsync(ready.Token);
        if (hello is null || !hello.Contains("\"ready\"", StringComparison.Ordinal))
        {
            Stop();
            return false;
        }
        return true;
    }

    /// <summary>Writes the bridge script next to the profile (versioned by content).</summary>
    private static string ScriptPath()
    {
        using var stream = typeof(LibreOfficeBridge).Assembly.GetManifestResourceStream("GetDocument.Office.lo_bridge.py")
            ?? throw new InvalidOperationException("Скрипт моста LibreOffice отсутствует в сборке.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string text = reader.ReadToEnd();
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GET-Document");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"lo_bridge-{hash}.py");
        if (!File.Exists(path)) File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private void Stop()
    {
        foreach (var process in new[] { _python, _office })
        {
            if (process is null) continue;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            process.Dispose();
        }
        _python = null;
        _office = null;
    }

    public void Dispose() => Stop();
}
