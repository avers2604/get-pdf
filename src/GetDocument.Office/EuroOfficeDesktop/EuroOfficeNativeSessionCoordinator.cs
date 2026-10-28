namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// One already-started, explicitly approved Qt-only diagnostic process.
/// It cannot load files, print or display a CEF editor through this interface.
/// </summary>
public interface IEuroOfficeNativeSessionTransport : IAsyncDisposable
{
    Task AttachAsync(CancellationToken cancellationToken);
    Task ResizeAsync(int width, int height, double scale, CancellationToken cancellationToken);
    Task CloseAsync(CancellationToken cancellationToken);
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Marshal these callbacks onto the host application's UI thread in the
/// Desktop adapter. The coordinator itself must never touch Avalonia controls.
/// </summary>
public interface IEuroOfficeNativeSessionView
{
    Task OpenAsync(Guid tabId, Guid sessionId, string documentId, string title,
        CancellationToken cancellationToken);
    Task ConnectedAsync(Guid tabId, Guid sessionId, CancellationToken cancellationToken);
    Task HostLostAsync(Guid tabId, Guid sessionId, CancellationToken cancellationToken);
    Task<bool> HasUnverifiedChangesAsync(Guid tabId, Guid sessionId,
        CancellationToken cancellationToken);
    Task RemoveAsync(Guid tabId, Guid sessionId, CancellationToken cancellationToken);
}

/// <summary>
/// Experimental Qt-only IPC coordinator. The session itself has no document
/// rights. All background exit notifications are generation/session checked.
/// A loss is visible in the tab and never silently restarts/commits an edit.
/// </summary>
public sealed class EuroOfficeNativeSessionCoordinator : IAsyncDisposable
{
    private sealed class Entry(
        Guid sessionId, IEuroOfficeNativeSessionTransport transport)
    {
        public Guid SessionId { get; } = sessionId;
        public IEuroOfficeNativeSessionTransport Transport { get; } = transport;
        public CancellationTokenSource StopMonitoring { get; } = new();
        public bool Faulted { get; set; }
        public bool Closing { get; set; }
        public bool TransportDisposed { get; set; }
    }

    private readonly IEuroOfficeNativeSessionView _view;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Entry> _entries = new();
    private bool _disposed;

    public EuroOfficeNativeSessionCoordinator(IEuroOfficeNativeSessionView view)
        => _view = view ?? throw new ArgumentNullException(nameof(view));

    public void AuthorizeDocumentOperations() =>
        throw new NotSupportedException(
            "Диагностический IPC не разрешает работу с документами, WORM или принтерами.");

    public async Task StartAsync(Guid tabId, Guid sessionId, string documentId,
        string title, IEuroOfficeNativeSessionTransport transport, bool labApproved,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (!labApproved)
            throw new NotSupportedException("Запуск диагностического Qt-сеанса не разрешён.");
        if (tabId == Guid.Empty || sessionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(documentId) || string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Некорректные ID и заголовок диагностического сеанса.");
        await _gate.WaitAsync(cancellationToken);
        bool opened = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.ContainsKey(tabId))
            {
                await transport.DisposeAsync();
                throw new InvalidOperationException("Для вкладки уже существует процесс.");
            }
            try
            {
                await transport.AttachAsync(cancellationToken);
                await _view.OpenAsync(tabId, sessionId, documentId, title, cancellationToken);
                opened = true;
                await _view.ConnectedAsync(tabId, sessionId, cancellationToken);
                var entry = new Entry(sessionId, transport);
                _entries.Add(tabId, entry);
                // No fire-and-forget exceptions escape: WatchAsync handles
                // unexpected process exits and cancellation itself.
                _ = WatchAsync(tabId, entry);
            }
            catch
            {
                await transport.DisposeAsync();
                if (opened)
                {
                    try { await _view.RemoveAsync(tabId, sessionId, CancellationToken.None); }
                    catch { /* failed cleanup never turns start into success */ }
                }
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task ResizeAsync(Guid tabId, Guid sessionId,
        int width, int height, double scale, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entry = RequireCurrent(tabId, sessionId);
            if (entry.Faulted || entry.Closing)
                throw new InvalidOperationException("Нативный сеанс не готов к изменению размеров.");
            try
            {
                await entry.Transport.ResizeAsync(width, height, scale, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or
                                        OperationCanceledException or ObjectDisposedException)
            {
                await FaultAsync(tabId, entry);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task CloseAsync(Guid tabId, Guid sessionId, bool confirmedDiscard,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entry = RequireCurrent(tabId, sessionId);
            if (await _view.HasUnverifiedChangesAsync(tabId, sessionId, cancellationToken) &&
                !confirmedDiscard)
                throw new InvalidOperationException(
                    "Есть неподтверждённые правки; нужно явное разрешение на их потерю.");
            entry.Closing = true;
            entry.StopMonitoring.Cancel();
            try
            {
                if (!entry.Faulted && !entry.TransportDisposed)
                    await entry.Transport.CloseAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or
                                        OperationCanceledException or ObjectDisposedException)
            {
                await FaultAsync(tabId, entry);
                throw;
            }
            finally { await DisposeTransportAsync(entry); }
            // Only remove the passive UI after process close/disposal.
            await _view.RemoveAsync(tabId, sessionId, CancellationToken.None);
            _entries.Remove(tabId);
            entry.StopMonitoring.Dispose();
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Safe external notification for a host that exited or lost IPC.
    /// Late notifications from closed or superseded sessions have no effect.
    /// </summary>
    public async Task ObserveHostExitAsync(Guid tabId, Guid sessionId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed || !_entries.TryGetValue(tabId, out var current) ||
                current.SessionId != sessionId || current.Closing || current.Faulted)
                return;
            await FaultAsync(tabId, current);
        }
        finally { _gate.Release(); }
    }

    private async Task WatchAsync(Guid tabId, Entry entry)
    {
        try
        {
            await entry.Transport.WaitForExitAsync(entry.StopMonitoring.Token);
            await ObserveHostExitAsync(tabId, entry.SessionId, CancellationToken.None);
        }
        catch (OperationCanceledException) when (entry.StopMonitoring.IsCancellationRequested)
        {
            // Expected when the session is closing or the app shuts down.
        }
        catch (Exception)
        {
            try { await ObserveHostExitAsync(tabId, entry.SessionId, CancellationToken.None); }
            catch { /* the UI may already be shutting down */ }
        }
    }

    private async Task FaultAsync(Guid tabId, Entry entry)
    {
        if (entry.Faulted) return;
        entry.Faulted = true;
        entry.StopMonitoring.Cancel();
        await DisposeTransportAsync(entry);
        try { await _view.HostLostAsync(tabId, entry.SessionId, CancellationToken.None); }
        catch { /* retain Failed, never interpret failed notification as recovery */ }
    }

    private static async Task DisposeTransportAsync(Entry entry)
    {
        if (entry.TransportDisposed) return;
        entry.TransportDisposed = true;
        await entry.Transport.DisposeAsync();
    }

    private Entry RequireCurrent(Guid tabId, Guid sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_entries.TryGetValue(tabId, out var entry))
            throw new KeyNotFoundException("Нативная вкладка не найдена.");
        if (entry.SessionId != sessionId)
            throw new InvalidDataException("Устаревшее сообщение другого нативного сеанса.");
        return entry;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (Entry entry in _entries.Values)
            {
                entry.Closing = true;
                entry.StopMonitoring.Cancel();
                await DisposeTransportAsync(entry);
                // No automatic saved-file claim or hidden host restart.
            }
            _entries.Clear();
            // Watchers are cancellation-aware and self-terminate; do not
            // dispose their CTS until their WaitForExitAsync unwinds.
        }
        finally { _gate.Release(); }
    }
}
