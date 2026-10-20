namespace GetPdf;

/// <summary>
/// Serializes PDF page renders and coalesces rapid zoom/navigation updates.
/// A second request is never silently dropped while the current one is loading.
/// </summary>
public sealed class PdfRenderRequests
{
    private bool _busy;
    private bool _pending;

    public bool TryBegin()
    {
        if (!_busy) { _busy = true; return true; }
        _pending = true;
        return false;
    }

    /// <returns>True if there was a request while rendering, requiring one more render.</returns>
    public bool Complete()
    {
        if (!_busy) throw new InvalidOperationException("Нет активного запроса отрисовки PDF.");
        _busy = false;
        bool again = _pending;
        _pending = false;
        return again;
    }

    public void Reset()
    {
        _busy = false;
        _pending = false;
    }
}
