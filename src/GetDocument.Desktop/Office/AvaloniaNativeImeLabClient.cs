using Avalonia;
using Avalonia.Input.TextInput;
using GetDocument.Office.EuroOfficeDesktop;

namespace GetDocument.Desktop.Office;

/// <summary>
/// Real Avalonia 12.1.4 TextInputMethodClient, strictly LAB-only.
/// This client will NOT be returned to the OS IME until a trusted CEF editor
/// caret telemetry adapter exists. A CEF frame or mouse position is not proof
/// of caret geometry and cannot activate this client.
/// </summary>
internal sealed class AvaloniaNativeImeLabClient : TextInputMethodClient, IDisposable
{
    private readonly Visual _preview;
    private readonly Guid _sessionId;
    private readonly Guid _tabId;
    private readonly Action<string?> _preeditChanged;
    private readonly Func<bool> _sessionActive;
    private Rect _cursor;
    private int _frame;
    private bool _hasCaret;
    private bool _disposed;

    public AvaloniaNativeImeLabClient(Visual preview, Guid sessionId, Guid tabId,
        Action<string?> preeditChanged, Func<bool> sessionActive)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(preeditChanged);
        ArgumentNullException.ThrowIfNull(sessionActive);
        if (sessionId == Guid.Empty || tabId == Guid.Empty)
            throw new ArgumentException("Пустой IME sessionId или tabId.");
        _preview = preview;
        _sessionId = sessionId;
        _tabId = tabId;
        _preeditChanged = preeditChanged;
        _sessionActive = sessionActive;
    }

    public bool CanRegisterWithOsIme =>
        !_disposed && _hasCaret && _sessionActive();

    public override Visual TextViewVisual => _preview;
    public override bool SupportsPreedit => true;
    public override bool SupportsSurroundingText => false;
    public override string SurroundingText => string.Empty;
    public override Rect CursorRectangle => _hasCaret ? _cursor : default;

    public override TextSelection Selection
    {
        get => new(0, 0);
        set
        {
            if (value.Start != 0 || value.End != 0)
                throw new NotSupportedException(
                    "CEF Lab не выдаёт окружающий текст или выделение.");
        }
    }

    /// <summary>
    /// Preedit is only for LOCAL visualization; never send it over IPC.
    /// An OS-produced TextInput does not authenticate a document edit either.
    /// </summary>
    public override void SetPreeditText(string? preeditText, int? cursorPos)
    {
        if (_disposed || !_sessionActive() || !_hasCaret)
            return;
        if (string.IsNullOrEmpty(preeditText))
        {
            _preeditChanged(null);
            return;
        }
        try { EuroOfficeImeLabTextPolicy.Validate(preeditText); }
        catch (ArgumentException)
        {
            _preeditChanged(null); // deny malformed preedit and drop previous
            return;
        }
        if (cursorPos is int caretOffset &&
            (caretOffset < 0 || caretOffset > preeditText.Length))
        {
            _preeditChanged(null);
            return;
        }
        _preeditChanged(preeditText);
    }

    public override void SetPreeditText(string? preeditText) =>
        SetPreeditText(preeditText, null);

    /// <summary>
    /// For a future authenticated CEF caret-telemetry adapter ONLY. Not
    /// called by the current OSR preview stream. A manually constructed
    /// sample is not provenance, so the pane does not expose this to users.
    /// </summary>
    internal void SupplyCaretFromFutureAuthenticatedNativeAdapter(
        EuroOfficeImeCaretPosition caret, double previewWidth,
        double previewHeight, int expectedFrameNumber)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_sessionActive()) throw new InvalidOperationException("CEF session ended.");
        var rect = EuroOfficeImeCaretGeometry.MapToPreview(
            caret, previewWidth, previewHeight,
            _sessionId, _tabId, expectedFrameNumber);
        if (_hasCaret && expectedFrameNumber <= _frame)
            throw new InvalidDataException("Повторная или устаревшая CEF caret геометрия.");
        _cursor = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
        _frame = expectedFrameNumber;
        _hasCaret = true;
        RaiseCursorRectangleChanged();
    }

    public void Revoke()
    {
        if (_disposed) return;
        _hasCaret = false;
        _frame = 0;
        _cursor = default;
        _preeditChanged(null);
        RequestReset();
    }

    public void Dispose()
    {
        Revoke();
        _disposed = true;
    }
}
