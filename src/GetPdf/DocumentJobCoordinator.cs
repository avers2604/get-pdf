namespace GetPdf;

internal enum DocumentJobKind { PdfProcessing }
internal enum DocumentJobPhase { Queued, Running, Progress, Completed, Cancelled, TimedOut, Failed }
internal sealed record DocumentJobUpdate(Guid Id, DocumentJobKind Kind,
    DocumentJobPhase Phase, int Completed = 0, int? Total = null);

internal sealed class DocumentJobContext
{
    private readonly Action<DocumentJobUpdate>? _observer;
    public Guid Id { get; } = Guid.NewGuid();
    public DocumentJobKind Kind { get; }
    public CancellationToken Cancellation { get; }

    internal DocumentJobContext(DocumentJobKind kind, CancellationToken cancellation,
        Action<DocumentJobUpdate>? observer)
    { Kind = kind; Cancellation = cancellation; _observer = observer; }

    internal void Notify(DocumentJobPhase phase, int completed = 0, int? total = null)
    {
        // Diagnostics/UI observers must never retain the gate or fail a document job.
        try { _observer?.Invoke(new(Id, Kind, phase, completed, total)); }
        catch (Exception) { }
    }

    public void Report(int completed, int? total = null)
    {
        if (completed < 0 || total is < 0 || total.HasValue && completed > total.Value)
            throw new ArgumentOutOfRangeException(nameof(completed));
        Cancellation.ThrowIfCancellationRequested();
        Notify(DocumentJobPhase.Progress, completed, total);
    }
}

/// <summary>
/// Process-wide admission for memory-heavy document work, including PDF windows. Capacity counts running + waiting jobs. Await deliberately
/// preserves the caller's synchronization context for WPF/NAPS2 image operations.
/// Timeouts are cooperative: never release a slot while native work is still running.
/// Callers must check Cancellation immediately before publishing an output file.
/// </summary>
internal sealed class DocumentJobCoordinator
{
    internal static DocumentJobCoordinator Shared { get; } = new();
    private readonly SemaphoreSlim _capacity;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly TimeSpan _waitTimeout;
    private readonly TimeSpan _runTimeout;

    public DocumentJobCoordinator(int maxPending = 4, TimeSpan? waitTimeout = null,
        TimeSpan? runTimeout = null)
    {
        if (maxPending is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maxPending));
        _waitTimeout = waitTimeout ?? TimeSpan.FromMinutes(2);
        _runTimeout = runTimeout ?? TimeSpan.FromHours(2);
        if (_waitTimeout <= TimeSpan.Zero || _waitTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));
        if (_runTimeout <= TimeSpan.Zero || _runTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(runTimeout));
        _capacity = new(maxPending, maxPending);
    }

    public async Task<T> RunAsync<T>(DocumentJobKind kind,
        Func<DocumentJobContext, Task<T>> work, CancellationToken cancellation = default,
        Action<DocumentJobUpdate>? observer = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        cancellation.ThrowIfCancellationRequested();
        if (!_capacity.Wait(0))
            throw new InvalidOperationException("Очередь документов заполнена. Завершите или отмените ожидающее задание.");
        using var deadline = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        var job = new DocumentJobContext(kind, linked.Token, observer);
        bool entered = false;
        try
        {
            job.Notify(DocumentJobPhase.Queued);
            entered = await _serial.WaitAsync(_waitTimeout, cancellation);
            if (!entered) throw new TimeoutException("Превышено время ожидания очереди документов.");
            cancellation.ThrowIfCancellationRequested();
            deadline.CancelAfter(_runTimeout);
            job.Notify(DocumentJobPhase.Running);
            var result = await work(job);
            // A callback may have already atomically published its result. It
            // owns the last cancellation checkpoint; do not relabel a commit.
            job.Notify(DocumentJobPhase.Completed);
            return result;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            job.Notify(DocumentJobPhase.TimedOut);
            throw new TimeoutException("Превышено время обработки документа; операция остановлена на безопасной границе.");
        }
        catch (OperationCanceledException) { job.Notify(DocumentJobPhase.Cancelled); throw; }
        catch (TimeoutException) { job.Notify(DocumentJobPhase.TimedOut); throw; }
        catch { job.Notify(DocumentJobPhase.Failed); throw; }
        finally
        {
            if (entered) _serial.Release();
            _capacity.Release();
        }
    }
}
