namespace GetPdf;
/// <summary>Owned scratch directory for a single PDF document.</summary>
internal sealed class PdfScratchSessionLifecycle : IDisposable
{
    private SessionStorage.SessionLease? _lease;
    public string? Folder => _lease?.Folder;
    public string Open()
    {
        if (_lease is not null) throw new InvalidOperationException("Временный PDF-сеанс уже открыт.");
        _lease = SessionStorage.CreateLeased();
        return _lease.Folder;
    }
    public void Close()
    {
        var lease = _lease;
        if (lease is null) return;
        _lease = null;
        SessionStorage.DeleteOwned(lease);
        lease.Dispose();
    }
    public void Dispose() => Close();
}
