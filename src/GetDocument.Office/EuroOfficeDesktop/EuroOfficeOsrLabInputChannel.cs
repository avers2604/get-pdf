using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// ONE-WAY input commands for a separately authorized, synthetic-only
/// EuroOffice CEF Wayland laboratory. The native process is not authenticated
/// by GUID alone; its executable and OS isolation need independent approval.
/// No ACK means no claim that CEF consumed or acted on a command.
/// </summary>
public sealed class EuroOfficeOsrLabInputChannel : IAsyncDisposable
{
    public const string Protocol = "getdoc-cef-osr-input/1";
    public const int MaximumMessages = 128;

    private readonly TextWriter _output;
    private readonly string _session;
    private readonly string _tab;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _sequence;
    private bool _disposed;
    private bool _focused;
    private bool _leftDown;
    private readonly bool _clickLabApproved;
    private readonly bool _keyboardLabApproved;
    private readonly bool _imeCommitLabApproved;
    public Guid SessionId { get; }
    public Guid TabId { get; }
    public bool ImeCommitLabApproved => _imeCommitLabApproved;

    private static readonly HashSet<string> AllowedNavigationKeys = new(StringComparer.Ordinal)
    {
        "ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown",
        "Home", "End", "PageUp", "PageDown", "Escape",
        "Backspace", "Delete", "Enter", "Tab"
    };

    public EuroOfficeOsrLabInputChannel(TextWriter output,
        Guid sessionId, Guid tabId, bool laboratoryApproved,
        bool clickLabApproved = false, bool keyboardLabApproved = false,
        bool imeCommitLabApproved = false)
    {
        if (!laboratoryApproved)
            throw new NotSupportedException(
                "Pointer/scroll/focus разрешены только на утверждённом CEF Wayland стенде.");
        ArgumentNullException.ThrowIfNull(output);
        if (sessionId == Guid.Empty || tabId == Guid.Empty)
            throw new ArgumentException("Требуются непустые sessionId и tabId.");
        _output = output;
        _session = sessionId.ToString("N");
        _tab = tabId.ToString("N");
        SessionId = sessionId;
        TabId = tabId;
        if (keyboardLabApproved && !clickLabApproved)
            throw new NotSupportedException("Текстовый ввод требует отдельного click-lab разрешения.");
        _clickLabApproved = clickLabApproved;
        if (imeCommitLabApproved && !keyboardLabApproved)
            throw new NotSupportedException("IME commit требует отдельного Keyboard Lab разрешения.");
        _keyboardLabApproved = keyboardLabApproved;
        _imeCommitLabApproved = imeCommitLabApproved;
    }

    /// <summary>True only inside this managed channel, not proof of CEF ACK.</summary>
    public bool ClickLabApproved => _clickLabApproved;
    public bool KeyboardLabApproved => _keyboardLabApproved;

    /// <summary>
    /// An atomic navigation key press/release pair. No system accelerators,
    /// Ctrl, Alt, Meta, clipboard, filesystem or generic key code accepted.
    /// </summary>
    public Task SendNavigationKeyAsync(string key, bool shift,
        CancellationToken cancellationToken)
    {
        if (!_keyboardLabApproved)
            throw new NotSupportedException("Навигация CEF требует отдельного GD-31J.16 разрешения.");
        if (key is null || !AllowedNavigationKeys.Contains(key))
            throw new ArgumentException("Клавиша не входит в список безопасной навигации.", nameof(key));
        return SendAsync("nav-key", new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["key"] = key,
            ["shift"] = shift
        }, cancellationToken, checkState: RequireKeyboardReady);
    }

    /// <summary>
    /// A SINGLE already-committed BMP character. No intermediate IME/preedit,
    /// paste, string payload, control characters or paired surrogates.
    /// </summary>
    public Task SendTextCharacterAsync(char character, CancellationToken cancellationToken)
    {
        if (!_keyboardLabApproved)
            throw new NotSupportedException("Текст CEF требует отдельного GD-31J.16 разрешения.");
        int codePoint = character;
        // Explicitly exclude C0/C1, DEL, surrogates, bidi overrides and
        // zero-width format controls; Unicode normalization is not inferred.
        if (codePoint < 0x20 || codePoint is >= 0x7f and <= 0x9f ||
            codePoint is >= 0xd800 and <= 0xdfff ||
            codePoint is >= 0x200b and <= 0x200f ||
            codePoint is >= 0x202a and <= 0x202e ||
            codePoint is >= 0x2060 and <= 0x2069 ||
            codePoint == 0xfeff)
            throw new ArgumentOutOfRangeException(nameof(character),
                "Запрещён служебный символ, суррогат или неполная IME-композиция.");
        return SendAsync("text-char", new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["codePoint"] = codePoint
        }, cancellationToken, checkState: RequireKeyboardReady);
    }

    /// <summary>
    /// Send ONLY the final 1–16 already-committed BMP codepoints. No preedit,
    /// string payload, clipboard or platform IME API travels over IPC.
    /// </summary>
    public Task SendImeCommittedTextAsync(string committed, CancellationToken cancellationToken)
    {
        if (!_imeCommitLabApproved)
            throw new NotSupportedException("GD-31J.17 IME Commit Lab не разрешён.");
        EuroOfficeImeLabTextPolicy.Validate(committed);
        int[] codes = committed.Select(ch => (int)ch).ToArray();
        return SendAsync("ime-commit", new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["codePoints"] = codes
        }, cancellationToken, checkState: RequireKeyboardReady);
    }

    private void RequireKeyboardReady()
    {
        RequireFocus();
        if (_leftDown)
            throw new InvalidOperationException("Ввод текста запрещён при удержании мыши.");
    }

    public void SendImeComposition() =>
        throw new NotSupportedException("IME preedit запрещён до отдельной проверенной реализации.");


    public Task SendPointerMoveAsync(double normalizedX, double normalizedY,
        CancellationToken cancellationToken) =>
        SendAsync("move", new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["x10000"] = Normalize(normalizedX),
            ["y10000"] = Normalize(normalizedY)
        }, cancellationToken, checkState: () => RequireFocus());

    public Task SendWheelAsync(double normalizedX, double normalizedY,
        int deltaX, int deltaY, CancellationToken cancellationToken)
    {
        int x = Normalize(normalizedX);
        int y = Normalize(normalizedY);
        if (deltaX is < -120 or > 120 || deltaY is < -120 or > 120 ||
            (deltaX == 0 && deltaY == 0))
            throw new ArgumentOutOfRangeException(nameof(deltaY),
                "Ограничение одного события прокрутки: [-120; 120].");
        return SendAsync("wheel", new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["deltaX"] = deltaX,
            ["deltaY"] = deltaY,
            ["x10000"] = x,
            ["y10000"] = y
        }, cancellationToken, checkState: () => RequireFocus());
    }

    public Task SendFocusAsync(bool focused, CancellationToken cancellationToken) =>
        SendAsync("focus", new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["focused"] = focused
        }, cancellationToken,
           checkState: () =>
           {
               if (!focused && _leftDown)
                   throw new InvalidOperationException(
                       "Перед потерей фокуса сначала завершите перетаскивание.");
           },
           commitState: () => _focused = focused);

    /// <summary>
    /// Separate GD-31J.15 permission: a left down/up pair only. CEF OSR
    /// must be on an approved disposable Wayland VM with synthetic files.
    /// </summary>
    public Task SendLeftButtonDownAsync(double normalizedX, double normalizedY,
        int clickCount, CancellationToken cancellationToken)
    {
        if (!_clickLabApproved)
            throw new NotSupportedException("Лабораторные клики не разрешены.");
        int x = Normalize(normalizedX), y = Normalize(normalizedY);
        if (clickCount is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(clickCount));
        return SendAsync("left-down", new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["clickCount"] = clickCount,
            ["x10000"] = x,
            ["y10000"] = y
        }, cancellationToken,
        checkState: () =>
        {
            RequireFocus();
            if (_leftDown)
                throw new InvalidOperationException("Нельзя повторить нажатие без отпускания.");
        },
        commitState: () =>
        {
            _leftDown = true;
        });
    }

    public Task SendLeftButtonUpAsync(double normalizedX, double normalizedY,
        CancellationToken cancellationToken)
    {
        if (!_clickLabApproved)
            throw new NotSupportedException("Лабораторные клики не разрешены.");
        int x = Normalize(normalizedX), y = Normalize(normalizedY);
        return SendAsync("left-up", new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["x10000"] = x,
            ["y10000"] = y
        }, cancellationToken,
        checkState: () =>
        {
            RequireFocus();
            if (!_leftDown)
                throw new InvalidOperationException("Отпускание без нажатия запрещено.");
        },
        commitState: () =>
        {
            _leftDown = false;
        });
    }

    private void RequireFocus()
    {
        if (!_focused)
            throw new InvalidOperationException("Для ввода требуется активный CEF focus.");
    }

    public void SendKeyboardOrIme() =>
        throw new NotSupportedException("Клавиатура и IME запрещены в GD-31J.14.");

    public void SendMouseClick() =>
        throw new NotSupportedException("Клики запрещены до проверки focus/CEF ввода.");

    public void ShareClipboard() =>
        throw new NotSupportedException("Буфер обмена запрещён до отдельной ИБ-проверки.");

    private async Task SendAsync(string command, SortedDictionary<string, object> fields,
        CancellationToken cancellationToken, Action? checkState = null,
        Action? commitState = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            checkState?.Invoke();
            if (_sequence >= MaximumMessages)
                throw new InvalidOperationException("Лимит событий лабораторного CEF сеанса исчерпан.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var request = new SortedDictionary<string, object>(StringComparer.Ordinal)
            {
                ["command"] = command,
                ["protocol"] = Protocol,
                ["sequence"] = _sequence + 1,
                ["sessionId"] = _session,
                ["tabId"] = _tab
            };
            foreach (var field in fields) request.Add(field.Key, field.Value);
            string serialized = JsonSerializer.Serialize(request);
            if (serialized.Length >= 4096)
                throw new InvalidDataException("Недопустимый размер команды CEF.");
            try
            {
                await _output.WriteLineAsync(serialized.AsMemory(), timeout.Token);
                await _output.FlushAsync(timeout.Token);
                ++_sequence;
                commitState?.Invoke();
            }
            catch
            {
                // A partial JSONL write cannot be safely retried with the
                // same sequence: permanently revoke this input channel.
                _disposed = true;
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private static int Normalize(double coordinate)
    {
        if (!double.IsFinite(coordinate) || coordinate < 0 || coordinate >= 1)
            throw new ArgumentOutOfRangeException(nameof(coordinate),
                "Позиция должна быть в диапазоне [0; 1) области CEF редактора.");
        return Math.Clamp((int)Math.Floor(coordinate * 10000), 0, 9999);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { _disposed = true; }
        finally { _gate.Release(); }
        // The owning supervisor controls OS pipes and process termination.
    }
}
