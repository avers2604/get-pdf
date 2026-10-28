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

    public EuroOfficeOsrLabInputChannel(TextWriter output,
        Guid sessionId, Guid tabId, bool laboratoryApproved)
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
    }

    public Task SendPointerMoveAsync(double normalizedX, double normalizedY,
        CancellationToken cancellationToken) =>
        SendAsync("move", new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["x10000"] = Normalize(normalizedX),
            ["y10000"] = Normalize(normalizedY)
        }, cancellationToken);

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
        }, cancellationToken);
    }

    public Task SendFocusAsync(bool focused, CancellationToken cancellationToken) =>
        SendAsync("focus", new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["focused"] = focused
        }, cancellationToken);

    public void SendKeyboardOrIme() =>
        throw new NotSupportedException("Клавиатура и IME запрещены в GD-31J.14.");

    public void SendMouseClick() =>
        throw new NotSupportedException("Клики запрещены до проверки focus/CEF ввода.");

    public void ShareClipboard() =>
        throw new NotSupportedException("Буфер обмена запрещён до отдельной ИБ-проверки.");

    private async Task SendAsync(string command, SortedDictionary<string, object> fields,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
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
