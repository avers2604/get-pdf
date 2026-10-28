using System.Diagnostics;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// GD-31J.8: supervised process wrapper for the Qt-only JSONL IPC experiment.
/// No CEF, native editor window, document path, save/print or access grant is
/// made through this process. Do not wire into production GET Document tabs.
/// </summary>
public sealed class EuroOfficeNativeIpcProcessSession : IEuroOfficeNativeSessionTransport
{
    private readonly Process _process;
    private readonly EuroOfficeNativeTabIpc _channel;
    private bool _disposed;

    private EuroOfficeNativeIpcProcessSession(Process process,
        EuroOfficeNativeTabIpc channel)
    {
        _process = process;
        _channel = channel;
    }

    public EuroOfficeNativeTabIpcState State => _channel.State;
    public bool IsRunning => !_disposed && !_process.HasExited;

    public static async Task<EuroOfficeNativeIpcProcessSession> StartAsync(
        EuroOfficeNativeLabHostPolicy policy, Guid sessionId, Guid tabId,
        bool labApproved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!labApproved)
            throw new NotSupportedException("IPC-хост EuroOffice ещё не прошёл лабораторную приёмку.");
        if (sessionId == Guid.Empty || tabId == Guid.Empty)
            throw new ArgumentException("Нужны идентификаторы сеанса и вкладки.");

        // CreateIpcStartInfo calls VerifyBinary(), enforcing previously
        // approved SHA-256 and local path, rather than trusting the process's
        // self-declared upstream version. TOCTOU/sandbox remain unresolved.
        var process = new Process { StartInfo = policy.CreateIpcStartInfo() };
        EuroOfficeNativeTabIpc? channel = null;
        bool started = false;
        try
        {
            started = process.Start();
            if (!started)
                throw new InvalidOperationException("Нативный Qt IPC хост не запустился.");
            channel = new EuroOfficeNativeTabIpc(
                process.StandardOutput, process.StandardInput, sessionId, tabId);
            await channel.ConnectAsync(cancellationToken);
            return new EuroOfficeNativeIpcProcessSession(process, channel);
        }
        catch
        {
            if (channel is not null)
                await channel.DisposeAsync();
            if (started)
                await KillAndReapAsync(process);
            process.Dispose();
            throw;
        }
    }

    public Task AttachAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _channel.AttachTabAsync(cancellationToken);
    }

    public Task ResizeAsync(int width, int height, double scale,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _channel.ResizeAsync(width, height, scale, cancellationToken);
    }

    /// <summary>
    /// Completes when the diagnostic Qt-only process exits. The coordinator
    /// uses this solely for host-loss detection, never as Office success ACK.
    /// </summary>
    public Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _process.WaitForExitAsync(cancellationToken);
    }

    public Task<EuroOfficeNativeDiagnosticFrame> RequestPreviewFrameAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _channel.RequestPreviewFrameAsync(cancellationToken);
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _channel.CloseAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await _process.WaitForExitAsync(timeout.Token);
            if (_process.ExitCode != 0)
                throw new InvalidDataException("Qt-хост завершился с ошибкой после закрытия вкладки.");
        }
        catch
        {
            await KillAndReapAsync(_process);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await KillAndReapAsync(_process);
        await _channel.DisposeAsync();
        _process.Dispose();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private static async Task KillAndReapAsync(Process process)
    {
        try
        {
            if (process.HasExited) return;
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { return; }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is InvalidOperationException or
                                   System.ComponentModel.Win32Exception or
                                   OperationCanceledException)
        {
            // No unbounded wait during failure cleanup. The operator's local
            // lab must still inspect for children if OS termination failed.
        }
    }
}
