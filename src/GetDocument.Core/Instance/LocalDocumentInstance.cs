using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GetDocument.Core.DocumentFiles;

namespace GetDocument.Core.Instance;

/// <summary>
/// Per-user, local-only desktop activation. The same-user pipe is never an
/// authentication mechanism for GUP-2; it accepts file paths, never URLs,
/// session cookies, tokens, script arguments or document contents.
/// </summary>
public sealed class LocalDocumentInstance : IAsyncDisposable
{
    private const int MaxBytes = 64 * 1024;
    private const int MaxFiles = 16;
    private static readonly TimeSpan IpcTimeout = TimeSpan.FromSeconds(3);
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private NamedPipeServerStream? _server;
    private Task? _listener;

    public LocalDocumentInstance(string pipeName)
    {
        if (pipeName.Length is < 8 or > 96 ||
            pipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Invalid local pipe name.", nameof(pipeName));
        _pipeName = pipeName;
    }

    /// <summary>
    /// A stable per-profile, non-secret name; no global machine-wide service.
    /// PipeOptions.CurrentUserOnly is the actual OS security boundary.
    /// </summary>
    public static LocalDocumentInstance ForCurrentProfile()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(profile))
            throw new InvalidOperationException("A user profile is required for local document IPC.");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(profile)));
        return new LocalDocumentInstance("get-document-v1-" +
            Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant());
    }

    /// <summary>Only one server can own this name. Never create a public HTTP listener.</summary>
    public bool TryBecomePrimary()
    {
        if (_server is not null) return true;
        if (_stop.IsCancellationRequested) return false;
        try
        {
            _server = new NamedPipeServerStream(
                _pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void Start(Action<IReadOnlyList<string>> onActivation)
    {
        ArgumentNullException.ThrowIfNull(onActivation);
        if (_server is null) throw new InvalidOperationException("Only the primary instance can listen.");
        if (_listener is not null) throw new InvalidOperationException("IPC listener already started.");
        _listener = ListenAsync(_server, onActivation);
    }

    /// <summary>
    /// Accept 0–16 supported local file paths, deduplicated in order.
    /// Classification is only a routing hint; the parser checks contents.
    /// </summary>
    public static IReadOnlyList<string> ValidateFiles(IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count > MaxFiles) throw new ArgumentException("Too many files in one launch.", nameof(files));
        var output = new List<string>(files.Count);
        var visited = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string input in files)
        {
            if (string.IsNullOrWhiteSpace(input) || input.Length > 32768 ||
                input.StartsWith("--", StringComparison.Ordinal) ||
                input.Contains("://", StringComparison.Ordinal))
                throw new ArgumentException("Only local document paths are accepted.", nameof(files));

            string absolute = Path.GetFullPath(input);
            if (DocumentFileClassifier.Classify(absolute).Kind == DocumentFileKind.Unsupported)
                throw new ArgumentException("Unsupported file type.", nameof(files));
            if (!File.Exists(absolute))
                throw new FileNotFoundException("Document not found.", absolute);
            if (visited.Add(absolute)) output.Add(absolute);
        }
        return output;
    }

    public async Task<bool> ForwardAsync(IReadOnlyList<string> files, CancellationToken cancellationToken = default)
    {
        if (_server is not null) throw new InvalidOperationException("The primary cannot forward to itself.");
        var paths = ValidateFiles(files);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(paths);
        if (payload.Length == 0 || payload.Length > MaxBytes)
            throw new ArgumentException("Launch payload too large.", nameof(files));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(IpcTimeout);
        try
        {
            await using var client = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeout.Token);
            byte[] header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            await client.WriteAsync(header, timeout.Token);
            await client.WriteAsync(payload, timeout.Token);
            await client.FlushAsync(timeout.Token);
            var reply = new byte[1];
            await client.ReadExactlyAsync(reply, timeout.Token);
            return reply[0] == 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false; // fail closed: never silently start a second window
        }
    }

    private async Task ListenAsync(NamedPipeServerStream server, Action<IReadOnlyList<string>> onActivation)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(_stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(IpcTimeout);

                var header = new byte[sizeof(int)];
                await server.ReadExactlyAsync(header, timeout.Token);
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is <= 0 or > MaxBytes) continue;
                var buffer = new byte[length];
                await server.ReadExactlyAsync(buffer, timeout.Token);
                var requested = JsonSerializer.Deserialize<string[]>(buffer);
                if (requested is null) continue;
                var paths = ValidateFiles(requested);
                onActivation(paths);
                await server.WriteAsync(new byte[] { 1 }, timeout.Token);
                await server.FlushAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                OperationCanceledException or JsonException or ArgumentException or NotSupportedException)
            {
                // Invalid same-user IPC request: ignore without logging sensitive paths.
            }
            finally
            {
                if (server.IsConnected)
                {
                    try { server.Disconnect(); }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stop.IsCancellationRequested) _stop.Cancel();
        if (_server is not null) await _server.DisposeAsync();
        if (_listener is not null)
        {
            try { await _listener; }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }
        _stop.Dispose();
    }
}
