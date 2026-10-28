namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// Shell-facing per-tab snapshot. Status is diagnostic only; it NEVER grants
/// permission to write to WORM, publish a new version or print.
/// </summary>
public sealed record EuroOfficeNativeTabSnapshot(
    string TabId, string DocumentId, string SessionId, long LastSequence,
    EuroOfficeNativeDocumentStatus Status, bool ModifiedNotificationObserved,
    bool SaveNotificationObserved, bool PrintNotificationObserved)
{
    public bool DocumentVerified => false;
    public bool SaveCompleted => false;
    public bool PdfReady => false;
    public bool PrintApproved => false;

    public string StatusText => Status switch
    {
        EuroOfficeNativeDocumentStatus.Requested => "EuroOffice: ожидание диагностического сеанса",
        EuroOfficeNativeDocumentStatus.ViewLoadedUnverified =>
            "EuroOffice: окно CEF загружено, документ не подтверждён",
        EuroOfficeNativeDocumentStatus.Closed => "EuroOffice: сеанс закрыт",
        EuroOfficeNativeDocumentStatus.Failed => "EuroOffice: ошибка нативного сеанса",
        _ => "EuroOffice: неподтверждённое состояние"
    };
}

/// <summary>
/// GD-31J.8: tabs are logical GET identifiers, not foreign HWND or QWidget*.
/// Exactly one native session per logical tab; no IPC event can cross tabs.
/// This does NOT launch Qt, own desktop windows, or bypass the existing
/// production LibreOffice editor.
/// </summary>
public sealed class EuroOfficeNativeTabRegistry
{
    private sealed class Entry(string tabId, string docId, string session)
    {
        public string TabId { get; } = tabId;
        public string DocumentId { get; } = docId;
        public string SessionId { get; } = session;
        public long LastSequence { get; set; }
        public EuroOfficeNativeDocumentWorkflow Flow { get; } = new(docId, session);
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _tabs = new(StringComparer.Ordinal);
    private readonly int _maxTabs;

    public EuroOfficeNativeTabRegistry(int maxTabs = 12)
    {
        if (maxTabs is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(maxTabs));
        _maxTabs = maxTabs;
    }

    public int Count { get { lock (_sync) return _tabs.Count; } }

    public void Register(string tabId, string documentId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(tabId) || tabId.Length > 128 ||
            string.IsNullOrWhiteSpace(documentId) || documentId.Length > 128 ||
            !Guid.TryParseExact(sessionId, "D", out Guid id) ||
            id == Guid.Empty || id.ToString("D") != sessionId)
            throw new ArgumentException("Неверные идентификаторы вкладки/документа/сеанса.");

        lock (_sync)
        {
            if (_tabs.ContainsKey(tabId) || _tabs.Values.Any(e => e.SessionId == sessionId))
                throw new InvalidOperationException("Вкладка или сеанс EuroOffice уже зарегистрированы.");
            if (_tabs.Count >= _maxTabs)
                throw new InvalidOperationException("Достигнут лимит нативных редакторов.");
            _tabs.Add(tabId, new Entry(tabId, documentId, sessionId));
        }
    }

    public void Apply(string tabId, EuroOfficeNativeIpcFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_sync)
        {
            Entry entry = Get(tabId);
            if (frame.SessionId != entry.SessionId ||
                frame.Sequence != entry.LastSequence + 1)
                throw new InvalidDataException("Чужая вкладка, повтор сообщения или пропуск последовательности.");
            if (frame.Kind == "event")
            {
                if (frame.EventName is null || frame.EventCode is null)
                    throw new InvalidDataException("Неполный IPC event.");
                entry.Flow.Observe(new EuroOfficeNativeEvent(
                    frame.EventName, frame.EventCode.Value, frame.SessionId));
            }
            else if (frame.Kind == "snapshot")
            {
                if (frame.Receipt is null)
                    throw new InvalidDataException("Отсутствует диагностический receipt.");
                entry.Flow.ApplyNativeReceipt(frame.Receipt, frame.SessionId);
            }
            else
                throw new InvalidDataException("Неизвестный тип IPC-пакета.");
            entry.LastSequence = frame.Sequence;
        }
    }

    public EuroOfficeNativeTabSnapshot Snapshot(string tabId)
    {
        lock (_sync)
        {
            Entry e = Get(tabId);
            return new EuroOfficeNativeTabSnapshot(
                e.TabId, e.DocumentId, e.SessionId, e.LastSequence,
                e.Flow.Status, e.Flow.ModifiedNotificationObserved,
                e.Flow.SaveNotificationObserved, e.Flow.PrintNotificationObserved);
        }
    }

    public void Abort(string tabId, string errorCode)
    {
        lock (_sync)
        {
            Entry entry = Get(tabId);
            entry.Flow.Fail(errorCode);
        }
    }

    public bool RemoveTerminal(string tabId)
    {
        lock (_sync)
        {
            Entry entry = Get(tabId);
            if (entry.Flow.Status is not (
                EuroOfficeNativeDocumentStatus.Closed or EuroOfficeNativeDocumentStatus.Failed))
                throw new InvalidOperationException("Нельзя удалить живую вкладку без закрытия хоста.");
            return _tabs.Remove(tabId);
        }
    }

    private Entry Get(string tabId) =>
        _tabs.TryGetValue(tabId, out Entry? entry)
            ? entry
            : throw new KeyNotFoundException("Нет зарегистрированной вкладки EuroOffice.");
}
