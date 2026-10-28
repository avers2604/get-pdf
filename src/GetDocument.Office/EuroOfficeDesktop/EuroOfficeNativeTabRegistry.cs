namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// Read-only view of one experimental native editor tab. A tab may be visible
/// but its Office document, revisions and printable PDF remain UNVERIFIED.
/// </summary>
public sealed record EuroOfficeNativeTabSnapshot(
    Guid TabId,
    Guid SessionId,
    string DocumentId,
    int Generation,
    EuroOfficeNativeDocumentStatus Status,
    bool ModifiedObserved,
    bool DocumentVerified = false,
    bool SaveCompleted = false,
    bool PdfReady = false);

/// <summary>
/// Tracks native host generations independently for every GET tab. This
/// does NOT embed Qt into Avalonia or grant access to source document paths.
///
/// A crashed process or a closed tab cannot be silently revived: a new
/// session identifier is mandatory. No event is accepted from an older host.
/// </summary>
public sealed class EuroOfficeNativeTabRegistry
{
    private sealed record Entry(
        Guid SessionId, string DocumentId, int Generation,
        EuroOfficeNativeDocumentWorkflow Workflow);

    private readonly object _sync = new();
    private readonly Dictionary<Guid, Entry> _tabs = new();

    public void Register(Guid tabId, Guid sessionId, string documentId)
    {
        ValidateIdentity(tabId, sessionId);
        if (string.IsNullOrWhiteSpace(documentId) || documentId.Length > 128)
            throw new ArgumentException("Недопустимый ID документа.");
        lock (_sync)
        {
            if (_tabs.ContainsKey(tabId))
                throw new InvalidOperationException("Вкладка уже зарегистрирована.");
            _tabs.Add(tabId, new Entry(sessionId, documentId, 1,
                new EuroOfficeNativeDocumentWorkflow(documentId, sessionId.ToString("N"))));
        }
    }

    public EuroOfficeNativeTabSnapshot Get(Guid tabId)
    {
        lock (_sync)
        {
            if (!_tabs.TryGetValue(tabId, out var entry))
                throw new KeyNotFoundException("Вкладка не найдена.");
            var workflow = entry.Workflow;
            return new EuroOfficeNativeTabSnapshot(
                tabId, entry.SessionId, entry.DocumentId, entry.Generation,
                workflow.Status, workflow.ModifiedNotificationObserved);
        }
    }

    public void ApplyReceipt(Guid tabId, Guid sessionId, EuroOfficeNativeReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        lock (_sync)
        {
            var entry = GetEntry(tabId, sessionId);
            entry.Workflow.ApplyNativeReceipt(receipt, sessionId.ToString("N"));
        }
    }

    public void HostCrashed(Guid tabId, Guid sessionId)
    {
        lock (_sync)
        {
            var entry = GetEntry(tabId, sessionId);
            entry.Workflow.Fail("native-host-crashed");
        }
    }

    public void Close(Guid tabId, Guid sessionId)
    {
        lock (_sync)
        {
            var entry = GetEntry(tabId, sessionId);
            entry.Workflow.Observe(
                new EuroOfficeNativeEvent("closed", 2001, sessionId.ToString("N")));
        }
    }

    /// <summary>
    /// Drop a closed/failed or explicitly discarded tab from the local
    /// registry. This does NOT stop the native process; the owning supervisor
    /// must dispose/reap it separately before user-facing tab removal.
    /// </summary>
    public void Remove(Guid tabId, Guid sessionId)
    {
        lock (_sync)
        {
            GetEntry(tabId, sessionId);
            _tabs.Remove(tabId);
        }
    }

    public void Restart(Guid tabId, Guid previousSessionId, Guid nextSessionId)
    {
        ValidateIdentity(tabId, previousSessionId);
        ValidateIdentity(tabId, nextSessionId);
        if (previousSessionId == nextSessionId)
            throw new InvalidOperationException("Повторное использование ID процесса запрещено.");
        lock (_sync)
        {
            var entry = GetEntry(tabId, previousSessionId);
            if (entry.Workflow.Status is not
                (EuroOfficeNativeDocumentStatus.Failed or EuroOfficeNativeDocumentStatus.Closed))
                throw new InvalidOperationException(
                    "Нельзя перезапустить активный редактор без закрытия и проверки изменений.");
            _tabs[tabId] = new Entry(nextSessionId, entry.DocumentId,
                checked(entry.Generation + 1),
                new EuroOfficeNativeDocumentWorkflow(entry.DocumentId, nextSessionId.ToString("N")));
        }
    }

    private Entry GetEntry(Guid tabId, Guid sessionId)
    {
        ValidateIdentity(tabId, sessionId);
        if (!_tabs.TryGetValue(tabId, out var entry))
            throw new KeyNotFoundException("Вкладка не найдена.");
        if (entry.SessionId != sessionId)
            throw new InvalidDataException("Устаревший процесс или чужой ID сеанса.");
        return entry;
    }

    private static void ValidateIdentity(Guid tabId, Guid sessionId)
    {
        if (tabId == Guid.Empty || sessionId == Guid.Empty)
            throw new ArgumentException("У вкладки и процесса должны быть непустые ID.");
    }
}

/// <summary>
/// Explicit adapters NOT YET CONNECTED. Future publication requires GUP-2
/// authorization, immutable CAS, file integrity, audit and server confirmation.
/// Printing requires an independently validated PDF and the GET wizard.
/// </summary>
public sealed class EuroOfficeNativeExternalGates
{
    public bool Gup2RevisionPublishAvailable => false;
    public bool GetPrintWizardTransferAvailable => false;

    public Task PublishRevisionToGup2Async(CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "Публикация в GUP-2 до доверенного CAS/ACL/WORM-адаптера запрещена.");

    public Task TransferPdfToGetPrintWizardAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "Передача PDF до проверенного экспорта и приёмки мастера печати запрещена.");
}
