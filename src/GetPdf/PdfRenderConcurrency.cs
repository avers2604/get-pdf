namespace GetPdf;

/// <summary>
/// Shared asynchronous throttle for NAPS2/PDFium decoding from multiple tabs.
/// Per-document locks still protect their individual native image objects.
/// A governor limits concurrency, NOT peak memory inside PDFium itself.
/// </summary>
internal static class PdfRenderConcurrency
{
    private static readonly SemaphoreSlim Gate = new(2, 2);
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
