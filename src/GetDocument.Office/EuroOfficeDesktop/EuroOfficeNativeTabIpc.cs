using System.Text;
using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// Diagnostic Qt-only IPC states. These are NOT document or CEF editor states.
/// </summary>
public enum EuroOfficeNativeTabIpcState
{
    New,
    Ready,
    Attached,
    Closed,
    Failed
}

/// <summary>
/// GD-31J.8: bounded, session-bound JSONL transport for the separate Qt
/// diagnostic process. Never transmits file paths, document bytes, tokens,
/// URLs, printer commands or window handles. This class does not start a host
/// or provide application-level authentication. A caller must launch and pin
/// the *exact* approved binary on an isolated workstation first.
/// </summary>
public sealed class EuroOfficeNativeTabIpc : IAsyncDisposable
{
    public const string Protocol = "getdoc-eurodesktop-ipc/1";
    private const int MaximumReplyChars = 4096;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _session;
    private readonly string _tab;
    private bool _disposed;
    private int _sequence;
    private EuroOfficeNativeTabIpcState _state = EuroOfficeNativeTabIpcState.New;

    public EuroOfficeNativeTabIpc(TextReader input, TextWriter output,
        Guid sessionId, Guid tabId)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (sessionId == Guid.Empty || tabId == Guid.Empty)
            throw new ArgumentException("Требуются непустые идентификаторы вкладки и процесса.");
        _input = input;
        _output = output;
        _session = sessionId.ToString("N");
        _tab = tabId.ToString("N");
    }

    public EuroOfficeNativeTabIpcState State => _state;

    public Task ConnectAsync(CancellationToken cancellationToken) =>
        ExchangeAsync(EuroOfficeNativeTabIpcState.New,
            EuroOfficeNativeTabIpcState.Ready,
            "hello", "hello-ack", "ready", null, cancellationToken);

    public Task AttachTabAsync(CancellationToken cancellationToken) =>
        ExchangeAsync(EuroOfficeNativeTabIpcState.Ready,
            EuroOfficeNativeTabIpcState.Attached,
            "attach-tab", "ack", "attached", null, cancellationToken);

    public Task ResizeAsync(int width, int height, double scale,
        CancellationToken cancellationToken)
    {
        if (width is < 1 or > 16384 || height is < 1 or > 16384 ||
            !double.IsFinite(scale) || scale is < 0.5 or > 4.0)
            throw new ArgumentOutOfRangeException(nameof(width), "Недопустимый размер или масштаб.");
        return ExchangeAsync(EuroOfficeNativeTabIpcState.Attached,
            EuroOfficeNativeTabIpcState.Attached,
            "resize", "ack", "resized",
            new SortedDictionary<string, object>(StringComparer.Ordinal)
            {
                ["height"] = height,
                ["scale"] = scale,
                ["width"] = width
            }, cancellationToken);
    }

    public Task CloseAsync(CancellationToken cancellationToken) =>
        ExchangeAsync(EuroOfficeNativeTabIpcState.Attached,
            EuroOfficeNativeTabIpcState.Closed,
            "close", "ack", "closed", null, cancellationToken);

    /// <summary>
    /// Request an original Qt-painter raster, NOT a CEF/editor frame.
    /// No file, URL, native handle, OS surface or printer data is transferred.
    /// </summary>
    public async Task<EuroOfficeNativeDiagnosticFrame> RequestPreviewFrameAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != EuroOfficeNativeTabIpcState.Attached)
                throw new InvalidOperationException("Нельзя получить кадр до подключения вкладки.");

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                timeout.Token.ThrowIfCancellationRequested();
                int sequence = ++_sequence;
                var request = new SortedDictionary<string, object>(StringComparer.Ordinal)
                {
                    ["command"] = "request-frame",
                    ["protocol"] = Protocol,
                    ["sequence"] = sequence,
                    ["sessionId"] = _session,
                    ["tabId"] = _tab
                };
                await _output.WriteLineAsync(
                    JsonSerializer.Serialize(request).AsMemory(), timeout.Token);
                await _output.FlushAsync(timeout.Token);
                string reply = await ReadBoundedLineAsync(timeout.Token,
                    EuroOfficeNativeDiagnosticFrame.MaximumReplyCharacters);
                return EuroOfficeNativeDiagnosticFrame.Parse(
                    reply, Guid.ParseExact(_session, "N"),
                    Guid.ParseExact(_tab, "N"), sequence);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or
                                       OperationCanceledException or ObjectDisposedException)
            {
                _state = EuroOfficeNativeTabIpcState.Failed;
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public void AuthorizeSaveOrPrint() =>
        throw new NotSupportedException("IPC вкладки не разрешает работу с файлами и принтерами.");

    private async Task ExchangeAsync(EuroOfficeNativeTabIpcState required,
        EuroOfficeNativeTabIpcState next, string command, string replyCommand,
        string replyState, SortedDictionary<string, object>? values,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != required)
                throw new InvalidOperationException("Некорректный порядок команд Qt-вкладки.");

            // No response, malformed response, cancellation or timeout MUST
            // invalidate this diagnostic session (no retries of stale IDs).
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                timeout.Token.ThrowIfCancellationRequested();
                int sequence = command == "hello" ? 0 : ++_sequence;
                var message = new SortedDictionary<string, object>(StringComparer.Ordinal)
                {
                    ["command"] = command,
                    ["protocol"] = Protocol,
                    ["sessionId"] = _session
                };
                if (command == "hello")
                    message["upstreamCommit"] = EuroOfficeDesktopProbeProtocol.PinnedUpstreamCommit;
                else
                {
                    message["sequence"] = sequence;
                    message["tabId"] = _tab;
                }
                if (values is not null)
                    foreach (var (key, value) in values)
                        message[key] = value;

                string request = JsonSerializer.Serialize(message);
                await _output.WriteLineAsync(request.AsMemory(), timeout.Token);
                await _output.FlushAsync(timeout.Token);

                string response = await ReadBoundedLineAsync(timeout.Token);
                ValidateReply(response, replyCommand, replyState, sequence, command == "hello");
                _state = next;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or
                                       OperationCanceledException or ObjectDisposedException)
            {
                _state = EuroOfficeNativeTabIpcState.Failed;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> ReadBoundedLineAsync(CancellationToken token,
        int maxChars = MaximumReplyChars)
    {
        var text = new StringBuilder(128);
        var buffer = new char[1];
        for (int i = 0; i < maxChars; i++)
        {
            int read = await _input.ReadAsync(buffer.AsMemory(), token);
            if (read == 0)
                throw new InvalidDataException("Qt-хост завершил IPC до подтверждения.");
            if (buffer[0] == '\n')
                return text.ToString();
            if (buffer[0] == '\r' || buffer[0] == '\0')
                throw new InvalidDataException("Недопустимый символ в IPC.");
            text.Append(buffer[0]);
        }
        throw new InvalidDataException("Ответ Qt-хоста превышает лимит.");
    }

    private void ValidateReply(string json, string replyCommand,
        string replyState, int sequence, bool hello)
    {
        try
        {
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 3
            });
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Ожидается JSON объект.");
            var expected = new HashSet<string>(StringComparer.Ordinal)
            {
                "command", "protocol", "sessionId", "sequence", "state"
            };
            if (hello) expected.Add("features");
            else expected.Add("tabId");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in root.EnumerateObject())
                if (!expected.Contains(prop.Name) || !seen.Add(prop.Name))
                    throw new InvalidDataException("Неизвестное или повторное поле IPC.");
            if (seen.Count != expected.Count ||
                root.GetProperty("command").GetString() != replyCommand ||
                root.GetProperty("protocol").GetString() != Protocol ||
                root.GetProperty("sessionId").GetString() != _session ||
                root.GetProperty("sequence").GetInt32() != sequence ||
                root.GetProperty("state").GetString() != replyState)
                throw new InvalidDataException("Несовпадающие идентификаторы или порядок событий.");
            if (hello)
            {
                var features = root.GetProperty("features");
                if (features.ValueKind != JsonValueKind.Array || features.GetArrayLength() != 0)
                    throw new InvalidDataException("Неизолированный Qt-хост объявил офисные полномочия.");
            }
            else if (root.GetProperty("tabId").GetString() != _tab)
                throw new InvalidDataException("Ответ предназначен другой вкладке.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or
                                   KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Некорректный JSONL от нативного Qt-хоста.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _state = EuroOfficeNativeTabIpcState.Closed;
            _disposed = true;
            // The process supervisor owns the streams and must kill/reap the
            // child process; we never own or tear down its OS handles here.
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
