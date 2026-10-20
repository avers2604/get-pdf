namespace GetPdf;

/// <summary>
/// Shared asynchronous throttle for NAPS2/PDFium decoding from multiple tabs.
/// Per-document locks still protect their individual native image objects.
/// A governor limits concurrency, NOT peak memory inside PDFium itself.
/// </summary>
internal static class PdfRenderConcurrency
{
    private static readonly SemaphoreSlim Gate = new(2, 2);
    private static readonly SemaphoreSlim ExclusiveQueue = new(1, 1);

    /// <summary>
    /// Reserve both native-import permits while the experimental Windows GDI
    /// renderer owns PDFium. Exclusive printers are serialized to avoid
    /// a two-print-job deadlock from two half-acquired permits.
    /// </summary>
    internal static async Task<IDisposable> EnterExclusiveAsync(
        CancellationToken token = default)
    {
        await ExclusiveQueue.WaitAsync(token).ConfigureAwait(false);
        bool first = false, second = false;
        try
        {
            await Gate.WaitAsync(token).ConfigureAwait(false);
            first = true;
            await Gate.WaitAsync(token).ConfigureAwait(false);
            second = true;
            return new ExclusiveLease();
        }
        catch
        {
            if (second) Gate.Release();
            if (first) Gate.Release();
            ExclusiveQueue.Release();
            throw;
        }
    }

    private sealed class ExclusiveLease : IDisposable
    {
        private int _returned;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _returned, 1) != 0) return;
            Gate.Release(2);
            ExclusiveQueue.Release();
        }
    }

    internal static async Task<IDisposable> EnterAsync(CancellationToken token = default)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        return new Lease();
    }

    private sealed class Lease : IDisposable
    {
        private int _returned;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0)
                Gate.Release();
        }
    }
}
