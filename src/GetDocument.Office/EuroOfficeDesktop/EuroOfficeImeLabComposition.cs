namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// Isolated, in-memory IME preedit state for a future OS input-method adapter.
/// Preedit text is NEVER forwarded to the CEF host. Only a separately approved
/// final commit reaches the synthetic Wayland CEF lab, with matching IDs.
/// Not a native IME preedit/candidate-window integration.
/// </summary>
public sealed class EuroOfficeImeLabComposition : IDisposable
{
    public const int MaxCommittedBmpUnits = 16;
    private const int MaxUpdatesPerComposition = 32;

    private readonly Guid _sessionId;
    private readonly Guid _tabId;
    private char[] _preedit = [];
    private int _updates;
    private bool _composing;
    private bool _disposed;

    public EuroOfficeImeLabComposition(Guid sessionId, Guid tabId, bool approved)
    {
        if (!approved)
            throw new NotSupportedException("IME Commit Lab требует отдельного разрешения.");
        if (sessionId == Guid.Empty || tabId == Guid.Empty)
            throw new ArgumentException("Нужны действительные идентификаторы CEF сеанса.");
        _sessionId = sessionId;
        _tabId = tabId;
    }

    public bool IsComposing => _composing;
    public int BufferedCharacters => _preedit.Length;

    public void Begin()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_composing) throw new InvalidOperationException("IME-композиция уже открыта.");
        Wipe();
        _composing = true;
        _updates = 0;
    }

    public void Update(string preedit)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_composing)
            throw new InvalidOperationException("IME preedit не был начат.");
        try { EuroOfficeImeLabTextPolicy.Validate(preedit); }
        catch
        {
            Cancel(); // invalid preedit cannot leave a previous draft armed
            throw;
        }
        if (_updates >= MaxUpdatesPerComposition)
        {
            Cancel();
            throw new InvalidOperationException("Слишком много обновлений IME preedit.");
        }
        ++_updates;
        Wipe();
        _preedit = preedit.ToCharArray();
    }

    public async Task CommitAsync(EuroOfficeOsrLabInputChannel channel,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(channel);
        if (!_composing || _preedit.Length == 0)
            throw new InvalidOperationException("Нет подтверждённого IME preedit для отправки.");
        try
        {
            if (channel.SessionId != _sessionId || channel.TabId != _tabId)
                throw new InvalidDataException("IME preedit относится к другому CEF сеансу.");
            await channel.SendImeCommittedTextAsync(new string(_preedit), cancellationToken);
        }
        finally
        {
            // No accidental cross-tab reuse or retained plaintext on success
            // or failure. A failed/partial pipe write revokes the channel.
            Cancel();
        }
    }

    public void Cancel()
    {
        Wipe();
        _composing = false;
        _updates = 0;
    }

    private void Wipe()
    {
        if (_preedit.Length != 0)
            Array.Clear(_preedit);
        _preedit = [];
    }

    public void Dispose()
    {
        Cancel();
        _disposed = true;
    }
}

/// <summary>
/// Same narrow Unicode rules as the native CEF synthetic-only IME receiver.
/// Reject control, surrogate, bidi/format, and oversized commit payloads.
/// </summary>
public static class EuroOfficeImeLabTextPolicy
{
    public static void Validate(string text)
    {
        if (string.IsNullOrEmpty(text) ||
            text.Length > EuroOfficeImeLabComposition.MaxCommittedBmpUnits)
            throw new ArgumentException("IME commit должен содержать 1–16 BMP символов.", nameof(text));
        foreach (char unit in text)
        {
            int code = unit;
            if (code < 0x20 || code is >= 0x7f and <= 0x9f ||
                code is >= 0xd800 and <= 0xdfff ||
                code is >= 0x200b and <= 0x200f ||
                code is >= 0x202a and <= 0x202e ||
                code is >= 0x2060 and <= 0x2069 ||
                code == 0xfeff)
                throw new ArgumentException("IME commit содержит запрещённый управляющий символ.", nameof(text));
        }
    }
}

/// <summary>
/// The OS clipboard, HTML/rich-text clipboard, and file-drop remain entirely
/// disabled in GD-31J.17. No data is read from or written to the OS clipboard.
/// </summary>
public static class EuroOfficeLabClipboardGate
{
    public static void RequireOsReadApproval() =>
        throw new NotSupportedException("Чтение буфера ОС не утверждено.");
    public static void RequireOsWriteApproval() =>
        throw new NotSupportedException("Запись в буфер ОС не утверждена.");
    public static void RequireHtmlOrRichTextApproval() =>
        throw new NotSupportedException("HTML/RTF/файлы не разрешены в лаборатории.");
}
