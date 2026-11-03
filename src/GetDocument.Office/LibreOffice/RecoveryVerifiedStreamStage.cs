using System.Buffers;
using System.Security.Cryptography;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// A receipt for bytes transferred into a caller-owned, unpublished staging
/// stream. This is NOT permission to overwrite a document, publish a WORM
/// version, or trust a path opened later.
/// </summary>
internal sealed record RecoveryStagedCopyReceipt(
    long Length, string Sha256, string Extension);

/// <summary>
/// GD-31J.27bf: one-use transfer of ownership of an already identity-gated
/// descriptor into a controlled staging operation. Actual native adapters
/// remain disabled. This class is not wired to the production restore flow.
/// </summary>
internal sealed class RecoveryVerifiedStreamStage : IDisposable, IAsyncDisposable
{
    private const long MaxCopyBytes = 128L * 1024 * 1024;
    private const int BufferBytes = 64 * 1024;

    private RecoveryNativeReadLease? _lease;
    private readonly RecoveryRelativeKey _key;

    // 0 = ready; 1 = copying; 2 = consumed; 3 = disposed.
    // No second transfer may reuse a descriptor or a partial stage.
    private int _state;

    private RecoveryVerifiedStreamStage(
        RecoveryNativeReadLease lease, RecoveryRelativeKey key)
    {
        _lease = lease;
        _key = key;
    }

    internal static RecoveryVerifiedStreamStage Open(
        IRecoveryNativeIdentityAdapter adapter, RecoveryRelativeKey key)
    {
        // The ONLY constructor path goes through the native evidence gate.
        // Windows/Linux real adapters currently throw PlatformNotSupported.
        RecoveryNativeReadLease lease =
            RecoveryNativeIdentityGate.OpenVerifiedRead(adapter, key);
        return new RecoveryVerifiedStreamStage(lease, key);
    }

    /// <summary>
    /// Copy the actual bytes read from the held descriptor into a NEW,
    /// empty, readable/writable/seekable staging stream. Hash the transferred
    /// bytes, require the expected manifest SHA, and independently rehash
    /// and validate ZIP/XML from that same staging stream. Return a receipt
    /// ONLY when all checks complete.
    ///
    /// The session owns/closes the source lease even on failure/cancellation.
    /// The caller owns staging; its contents are NEVER published by this API.
    /// On failure, best-effort truncate a stage that we verified was empty
    /// before copying. A failed truncate means it must be discarded by caller.
    /// </summary>
    internal async Task<RecoveryStagedCopyReceipt> StageToAsync(
        Stream staging, string expectedSha256, string extension,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _state) == 3)
            throw new ObjectDisposedException(nameof(RecoveryVerifiedStreamStage));
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException(
                "Дескриптор аварийной копии допускает только одну операцию передачи.");

        bool privateEmptyStaging = false;
        try
        {
            ArgumentNullException.ThrowIfNull(staging);
            if (!staging.CanRead || !staging.CanWrite || !staging.CanSeek ||
                staging.Length != 0 || staging.Position != 0)
                throw new ArgumentException(
                    "Временный поток должен быть новым, пустым, читаемым и доступным для записи.",
                    nameof(staging));

            if (extension is not (".docx" or ".xlsx" or ".pptx") ||
                !_key.CopyFileName.EndsWith(extension, StringComparison.Ordinal))
                throw new ArgumentException(
                    "Формат временной копии не совпадает с защищённым именем.",
                    nameof(extension));

            if (expectedSha256 is null || expectedSha256.Length != 64 ||
                !expectedSha256.All(Uri.IsHexDigit))
                throw new ArgumentException(
                    "Ожидается полный SHA-256 из проверенного манифеста.",
                    nameof(expectedSha256));

            // Only from this point may failure cleanup truncate the stage:
            // the caller-provided stream was VERIFIED EMPTY beforehand.
            privateEmptyStaging = true;
            cancellationToken.ThrowIfCancellationRequested();

            Stream source = _lease?.Stream
                ?? throw new ObjectDisposedException(nameof(RecoveryVerifiedStreamStage));
            source.Position = 0;

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
            long copied = 0;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int allowed = (int)Math.Min(
                        buffer.Length, MaxCopyBytes - copied + 1);
                    int read = await source.ReadAsync(
                        buffer.AsMemory(0, allowed), cancellationToken);
                    if (read == 0) break;

                    copied += read;
                    if (copied > MaxCopyBytes)
                        throw new InvalidDataException(
                            "Превышен предел аварийной копии в 128 МиБ.");

                    await staging.WriteAsync(
                        buffer.AsMemory(0, read), cancellationToken);
                    hasher.AppendData(buffer, 0, read);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (copied == 0)
                    throw new InvalidDataException("Пустая аварийная копия.");
                await staging.FlushAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                string digest = Convert.ToHexString(hasher.GetHashAndReset());
                if (!string.Equals(digest, expectedSha256,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "Данные, фактически переданные во временную копию, не совпали с SHA-256.");

                // Verifies actual staged bytes (not a path and not merely
                // earlier source metadata), including SHA-256 + ZIP/XML.
                staging.Position = 0;
                if (!OfficeRecoveryStore.VerifyOpenedPackage(
                        staging, extension, digest))
                    throw new InvalidDataException(
                        "Временная копия не выдержала независимую проверку SHA-256.");
                cancellationToken.ThrowIfCancellationRequested();

                staging.Position = 0;
                if (Interlocked.CompareExchange(ref _state, 2, 1) != 1)
                    throw new ObjectDisposedException(nameof(RecoveryVerifiedStreamStage));

                // A receipt is not published anywhere and does not expose
                // a pathname. The caller still must secure staging itself.
                return new RecoveryStagedCopyReceipt(copied, digest, extension);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }
        catch
        {
            if (privateEmptyStaging)
                TryResetPrivateStaging(staging);
            Interlocked.CompareExchange(ref _state, 2, 1);
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _lease, null)?.Dispose();
        }
    }

    private static void TryResetPrivateStaging(Stream staging)
    {
        try
        {
            if (staging.CanSeek && staging.CanWrite)
            {
                staging.SetLength(0);
                staging.Position = 0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or NotSupportedException or ObjectDisposedException
                                   or System.Security.SecurityException)
        {
            // The stage remains UNTRUSTED and caller-owned if cleanup fails.
            // Preserve the original copy/validation/cancellation exception.
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _state, 3);
        Interlocked.Exchange(ref _lease, null)?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
