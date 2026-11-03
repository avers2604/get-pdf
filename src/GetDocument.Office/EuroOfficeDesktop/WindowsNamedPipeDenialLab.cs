using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// GD-31J.27ak: Windows-only, deny-only named-pipe transport observation.
/// This is not used by production SaveAs, does not transfer a file HANDLE,
/// and cannot authorize any document operation. Only a disposable offline
/// Windows VM may run the manually started external CLI roles.
/// </summary>
public static class WindowsNamedPipeDenialLab
{
    private const string RequestText =
        "GETDOC_GD31J27AK_WINDOWS_PIPE_DEVNULL_FREE_DENIAL_ONLY\n";
    private const string DeniedText =
        "{\"state\":\"denied\",\"laboratoryOnly\":true," +
        "\"nativePublicationAuthorized\":false,\"releaseReady\":false," +
        "\"writePermissionGranted\":false}\n";

    public static byte[] Request => Encoding.ASCII.GetBytes(RequestText);
    public static byte[] Denied => Encoding.ASCII.GetBytes(DeniedText);

    public static bool NativePublicationAuthorized => false;
    public static bool ReleaseReady => false;

    public static string PipeName(Guid runId)
    {
        if (runId == Guid.Empty)
            throw new ArgumentException("A nonempty one-shot run identifier is required.", nameof(runId));
        return "getdoc-gd31j27ak-denial-" + runId.ToString("N");
    }

    public static void RequireExactRequest(byte[]? packet)
    {
        if (packet is null || !packet.AsSpan().SequenceEqual(Request))
            throw new InvalidDataException("Unknown or overlong Windows denial-only packet.");
    }

    public static void RequireExactDenial(byte[]? packet)
    {
        if (packet is null || !packet.AsSpan().SequenceEqual(Denied))
            throw new InvalidDataException("Windows laboratory peer did not return exact denial.");
    }

    public static void RequireMatchingClientPid(
        uint observedPid, int pinnedPid, int serverPid)
    {
        // Operator-supplied PID is only a *comparison target*, not process
        // trust. A live kernel-connected named-pipe peer is queried below.
        if (pinnedPid < 5 || pinnedPid == serverPid ||
            observedPid != (uint)pinnedPid)
            throw new InvalidDataException("Kernel-connected pipe peer PID mismatch.");
    }

    /// <summary>
    /// Exactly one Windows peer may connect under the CURRENT USER's pipe ACL.
    /// The kernel-reported connected PID must match the separately entered
    /// expected PID. Even a matching PID gives no trusted executable proof.
    /// </summary>
    public static async Task ServeDeniedOnceAsync(
        Guid runId, int pinnedClientPid, CancellationToken cancellationToken = default)
    {
        RequireWindows();
        string pipeName = PipeName(runId);
        if (pinnedClientPid < 5 || pinnedClientPid == Environment.ProcessId)
            throw new InvalidDataException("Invalid external Windows client PID.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var pipe = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Message,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);

        await pipe.WaitForConnectionAsync(timeout.Token);
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint connectedPid))
            throw new InvalidDataException("Connected Windows pipe peer PID unavailable.");
        RequireMatchingClientPid(connectedPid, pinnedClientPid, Environment.ProcessId);

        byte[] request = await ReadSingleMessageAsync(pipe, Request.Length, timeout.Token);
        RequireExactRequest(request);
        await pipe.WriteAsync(Denied, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
    }

    /// <summary>
    /// This client sends only one static non-document packet. A valid reply
    /// is still DENIED; this role never opens files or reports save success.
    /// </summary>
    public static async Task ReceiveDenialOnceAsync(
        Guid runId, CancellationToken cancellationToken = default)
    {
        RequireWindows();
        string pipeName = PipeName(runId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(30_000, timeout.Token);
        // On Windows, changing client ReadMode to Message can require
        // FILE_WRITE_ATTRIBUTES, which .NET's standard client handle may
        // not possess. Read the fixed reply in byte mode instead.
        await pipe.WriteAsync(Request, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
        byte[] response = await ReadDenialToEofAsync(pipe, timeout.Token);
        RequireExactDenial(response);
    }

    /// <summary>
    /// Client-side bounded byte reader. Never changes PipeStream.ReadMode.
    /// A reply is accepted ONLY after clean EOF, so an exact-prefix reply
    /// followed by a hidden extra byte, a hanging peer or timeout fails.
    /// This method has no OS dependency and can be exercised on MemoryStream.
    /// </summary>
    internal static async Task<byte[]> ReadDenialToEofAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] buffer = new byte[Denied.Length + 1];
        int count = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int received = await stream.ReadAsync(
                buffer.AsMemory(count, buffer.Length - count), cancellationToken);
            if (received == 0) break;
            count += received;
            if (count > Denied.Length)
                throw new InvalidDataException("Overlong Windows pipe denial response.");
        }
        if (count != Denied.Length)
            throw new InvalidDataException("Truncated Windows pipe denial response.");
        return buffer[..count];
    }

    /// <summary>Server-side message reader; server created in Message mode.</summary>
    private static async Task<byte[]> ReadSingleMessageAsync(
        PipeStream pipe, int expectedLength, CancellationToken cancellationToken)
    {
        // Read no more than the expected message length + one sentinel byte.
        // Excess data, message fragmentation and early EOF never become ACK.
        byte[] buffer = new byte[expectedLength + 1];
        int count = 0;
        while (true)
        {
            int received = await pipe.ReadAsync(
                buffer.AsMemory(count, buffer.Length - count), cancellationToken);
            if (received <= 0)
                throw new InvalidDataException("Windows named pipe ended before denial.");
            count += received;
            if (count > expectedLength)
                throw new InvalidDataException("Overlong Windows denial-only message.");
            if (pipe.IsMessageComplete)
                break;
            if (count == expectedLength)
                throw new InvalidDataException("Trailing Windows named-pipe bytes prohibited.");
        }
        if (count != expectedLength)
            throw new InvalidDataException("Truncated Windows denial-only message.");
        return buffer[..count];
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows-only offline named pipe lab.");
    }

    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeClientProcessId",
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe, out uint clientProcessId);
}
