namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// Passive diagnostic tab state for the existing GET single tab strip.
/// Not a Qt/CEF window handle and not an authorization to edit, save or print.
/// </summary>
public sealed record EuroOfficeNativeWorkspaceTabState(
    Guid TabId,
    Guid SessionId,
    string DocumentId,
    string Title,
    int Generation,
    bool Selected,
    EuroOfficeNativeDocumentStatus Status,
    bool ModifiedObserved,
    bool DocumentVerified = false,
    bool SaveCompleted = false,
    bool PdfReady = false,
    bool PrintApproved = false);

/// <summary>
/// Maintains native *diagnostic* tabs alongside the existing LibreOffice/PDF
/// tabs. It does not launch processes, open files, create UI controls or
/// promote any native notifications into verified document operations.
/// </summary>
public sealed class EuroOfficeNativeWorkspaceTabs
{
    private readonly object _sync = new();
    private readonly EuroOfficeNativeTabRegistry _registry = new();
    private readonly Dictionary<Guid, string> _titles = new();
    private Guid? _active;

    public int TabCount { get { lock (_sync) return _titles.Count; } }
    public Guid? ActiveTabId { get { lock (_sync) return _active; } }

    public void Register(Guid tabId, Guid sessionId, string documentId, string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 160)
            throw new ArgumentException("Не указано безопасное название вкладки.", nameof(title));
        lock (_sync)
        {
            if (_titles.ContainsKey(tabId))
                throw new InvalidOperationException("Диагностическая вкладка уже существует.");
            _registry.Register(tabId, sessionId, documentId);
            _titles.Add(tabId, title);
        }
    }

    public EuroOfficeNativeWorkspaceTabState Get(Guid tabId)
    {
        lock (_sync)
        {
            if (!_titles.TryGetValue(tabId, out var title))
                throw new KeyNotFoundException("Нет такой диагностической вкладки.");
            var state = _registry.Get(tabId);
            return new EuroOfficeNativeWorkspaceTabState(
                tabId, state.SessionId, state.DocumentId, title,
                state.Generation, _active == tabId, state.Status,
                state.ModifiedObserved);
        }
    }

    public void Select(Guid tabId)
    {
        lock (_sync)
        {
            if (!_titles.ContainsKey(tabId))
                throw new KeyNotFoundException("Нет такой диагностической вкладки.");
            _active = tabId;
        }
    }

    public void Deselect()
    {
        lock (_sync) _active = null;
    }

    public void ApplyReceipt(Guid tabId, Guid sessionId, EuroOfficeNativeReceipt receipt)
    {
        lock (_sync)
        {
            RequireTab(tabId);
            _registry.ApplyReceipt(tabId, sessionId, receipt);
        }
    }

    public void HostCrashed(Guid tabId, Guid sessionId)
    {
        lock (_sync)
        {
            RequireTab(tabId);
            _registry.HostCrashed(tabId, sessionId);
        }
    }

    public void Restart(Guid tabId, Guid previousSessionId, Guid nextSessionId)
    {
        lock (_sync)
        {
            RequireTab(tabId);
            _registry.Restart(tabId, previousSessionId, nextSessionId);
        }
    }

    public void Close(Guid tabId, Guid sessionId)
    {
        lock (_sync)
        {
            RequireTab(tabId);
            var current = _registry.Get(tabId);
            if (current.SessionId != sessionId)
                throw new InvalidDataException("Вкладка принадлежит другому сеансу.");
            // Closing a crashed process is allowed. A clean running process
            // must be stopped by its owner BEFORE this passive UI cleanup.
            if (current.Status is not
                (EuroOfficeNativeDocumentStatus.Failed or EuroOfficeNativeDocumentStatus.Closed))
                _registry.Close(tabId, sessionId);
            _registry.Remove(tabId, sessionId);
            _titles.Remove(tabId);
            if (_active == tabId) _active = null;
        }
    }

    private void RequireTab(Guid tabId)
    {
        if (!_titles.ContainsKey(tabId))
            throw new KeyNotFoundException("Диагностическая вкладка отсутствует.");
    }
}
