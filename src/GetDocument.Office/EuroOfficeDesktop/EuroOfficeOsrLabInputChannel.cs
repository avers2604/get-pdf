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

    public EuroOfficeOsrLabInputChannel(TextWriter output,
        Guid sessionId, Guid tabId, bool laboratoryApproved,
        bool clickLabApproved = false)
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
        _clickLabApproved = clickLabApproved;
    }

    /// <summary>True only inside this managed channel, not proof of CEF ACK.</summary>
    public bool ClickLabApproved => _clickLabApproved;

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
