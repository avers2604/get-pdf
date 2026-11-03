namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27bi — privately owned, file-backed consumer buffer, not a saved
/// document. There is NO public pathname or FileStream API. The only
/// consumer access is a single, scoped, read-only callback.
/// Production recovery and native adapters remain disabled/unwired.
/// </summary>
internal sealed class RecoveryOwnedConsumerOutput : IDisposable, IAsyncDisposable
{
    private const long MaxRecoveryCopyBytes = 128L * 1024 * 1024;
    private FileStream? _heldBuffer;
    private readonly string _privateDirectory;
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    // 0 = available; 1 = callback active; 2 = consumed; 3 = disposed.
    // Dispose during callback cancels, but the callback's finally closes
    // the held descriptor so it cannot be closed while reading.
    private int _state;
    private int _cleanupStarted;

    internal RecoveryStagedCopyReceipt Receipt { get; }

    private RecoveryOwnedConsumerOutput(
        FileStream heldBuffer, string privateDirectory, RecoveryStagedCopyReceipt receipt)
    {
        _heldBuffer = heldBuffer;
        _privateDirectory = privateDirectory;
        Receipt = receipt;
    }

    /// <summary>
    /// Consume ownership of a private publication. Transfer to a DIFFERENT,
    /// newly-created file backed by one held handle. Use DeleteOnClose rather
    /// than reopening the pathname for deletion; unsupported filesystems fail
    /// closed without fallback to path-based deletion.
    /// </summary>
    internal static async Task<RecoveryOwnedConsumerOutput> CreateAsync(
        RecoveryPrivatePublication publication,
        CancellationToken cancellationToken = default,
        Action<string>? onPrivateDirectoryCreated = null)
    {
        ArgumentNullException.ThrowIfNull(publication);
        using (publication)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryInfo directory =
                Directory.CreateTempSubdirectory("getdoc-consumer-owned-");
            string root = directory.FullName;
            FileStream? held = null;
            try
            {
                RequireRegularPrivateDirectory(root);
                onPrivateDirectoryCreated?.Invoke(root); // internal test-only seam
                RequireRegularPrivateDirectory(root);
                cancellationToken.ThrowIfCancellationRequested();

                string filename = Guid.NewGuid().ToString("N") + ".consumer";
                string path = Path.Combine(root, filename);
                held = new FileStream(path, FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.None, bufferSize: 65536,
                    options: FileOptions.Asynchronous | FileOptions.DeleteOnClose);

                RecoveryStagedCopyReceipt receipt =
                    await publication.ConsumeVerifiedToAsync(held, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                // The consumer owner itself verifies the actual file-backed
                // destination on the SAME handle before returning.
                held.Flush(flushToDisk: true);
                if (!held.CanRead || !held.CanSeek ||
                    held.Length != receipt.Length ||
                    held.Length == 0 || held.Length > MaxRecoveryCopyBytes)
                    throw new InvalidDataException(
                        "Временный приёмный файл не соответствует проверенной квитанции.");

                held.Position = 0;
                if (!OfficeRecoveryStore.VerifyOpenedPackage(
                        held, receipt.Extension, receipt.Sha256))
                    throw new InvalidDataException(
                        "Приёмный файл не прошёл SHA-256 и ZIP/XML-проверку.");
                cancellationToken.ThrowIfCancellationRequested();

                // No output filepath is returned or exposed to callbacks.
                var output = new RecoveryOwnedConsumerOutput(held, root, receipt);
                held = null; // ownership moved, DeleteOnClose remains armed
                return output;
            }
            catch
            {
                held?.Dispose(); // OS-backed DeleteOnClose; no pathname deletion
                TryRemoveEmptyDirectory(root);
                throw;
            }
        }
    }

    /// <summary>
    /// A single callback sees a revocable read-only Stream wrapper rather
    /// than the mutable FileStream or its path. Verify SHA/ZIP immediately
    /// before and after the callback; do not allow the caller to treat
    /// this as permission to publish or overwrite a user/WORM document.
    /// </summary>
    internal async Task<T> ReadVerifiedAsync<T>(
        Func<Stream, CancellationToken, Task<T>> reader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (Volatile.Read(ref _state) == 3)
            throw new ObjectDisposedException(nameof(RecoveryOwnedConsumerOutput));
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException(
                "Временный приёмный файл уже используется либо обработан.");

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetimeCancellation.Token);
            CancellationToken abort = linked.Token;
            abort.ThrowIfCancellationRequested();
            FileStream source = _heldBuffer
                ?? throw new ObjectDisposedException(nameof(RecoveryOwnedConsumerOutput));
            if (!source.CanRead || !source.CanSeek || source.Length != Receipt.Length ||
                source.Length == 0 || source.Length > MaxRecoveryCopyBytes)
                throw new InvalidDataException(
                    "Дескриптор временного приёмного файла изменился.");

            source.Position = 0;
            if (!OfficeRecoveryStore.VerifyOpenedPackage(
                    source, Receipt.Extension, Receipt.Sha256))
                throw new InvalidDataException(
                    "Приёмный файл больше не соответствует SHA/ZIP-квитанции.");
            abort.ThrowIfCancellationRequested();

            source.Position = 0;
            using var view = new RevocableReadOnlyStream(source, abort);
            T result = await reader(view, abort);
            // Revoke access immediately when callback returns, BEFORE
            // post-read SHA/ZIP verification. A retained wrapper cannot
            // read concurrently with this final integrity check.
            view.Dispose();
            abort.ThrowIfCancellationRequested();

            // A consumer might have read an inconsistent or mutated stream.
            // Recheck the SAME owned handle before returning any result.
            source.Position = 0;
            if (!OfficeRecoveryStore.VerifyOpenedPackage(
                    source, Receipt.Extension, Receipt.Sha256))
                throw new InvalidDataException(
                    "Приёмный файл изменился во время чтения.");
            abort.ThrowIfCancellationRequested();

            if (Interlocked.CompareExchange(ref _state, 2, 1) != 1)
                throw new ObjectDisposedException(nameof(RecoveryOwnedConsumerOutput));

            // The result is only a read-side observation. It does not confer
            // safe OS-native identity, WORM commit or replacement rights.
            return result;
        }
        finally
        {
            // The caller-view is revoked before this finally exits.
            // Any error or cancellation also consumes the owner.
            CloseAndCleanup();
        }
    }

    private static void RequireRegularPrivateDirectory(string directory)
    {
        for (DirectoryInfo? part = new(directory);
             part is not null; part = part.Parent)
        {
            part.Refresh();
            if (!part.Exists || part.LinkTarget is not null ||
                (part.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    "Закрытый каталог приёмного файла недоступен или содержит ссылку.");
        }
    }

    private static void TryRemoveEmptyDirectory(string directory)
    {
        try
        {
            RequireRegularPrivateDirectory(directory);
            Directory.Delete(directory, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException
                                   or InvalidDataException or DirectoryNotFoundException)
        {
            // Only the empty generated directory may be removed. Never
            // recurse into unknown or possibly foreign paths.
        }
    }

    public void Dispose()
    {
        int previous = Interlocked.Exchange(ref _state, 3);
        if (previous == 3) return;
        try { _lifetimeCancellation.Cancel(); }
        catch (ObjectDisposedException) { }

        if (previous != 1)
            CloseAndCleanup(); // callback owns active handle until finally
    }

    private void CloseAndCleanup()
    {
        if (Interlocked.Exchange(ref _cleanupStarted, 1) != 0) return;
        FileStream? held = Interlocked.Exchange(ref _heldBuffer, null);
        try
        {
            held?.Dispose(); // OS deletes held file: no pathname-based delete
        }
        finally
        {
            _lifetimeCancellation.Dispose();
            TryRemoveEmptyDirectory(_privateDirectory);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// A revocable stream wrapper whose consumer cannot cast to FileStream,
    /// obtain its Name/handle or write into the owned data. Retaining the
    /// wrapper beyond its callback yields ObjectDisposedException.
    /// </summary>
    private sealed class RevocableReadOnlyStream : Stream
    {
        private Stream? _source;
        private readonly CancellationToken _ownerCancellation;

        internal RevocableReadOnlyStream(Stream source, CancellationToken ownerCancellation)
        {
            _source = source;
            _ownerCancellation = ownerCancellation;
        }

        private Stream Check()
        {
            Stream inner = Volatile.Read(ref _source)
                ?? throw new ObjectDisposedException(nameof(RevocableReadOnlyStream));
            _ownerCancellation.ThrowIfCancellationRequested();
            return inner;
        }

        public override bool CanRead => Volatile.Read(ref _source)?.CanRead == true;
        public override bool CanSeek => Volatile.Read(ref _source)?.CanSeek == true;
        public override bool CanWrite => false;

        public override long Length => Check().Length;

        public override long Position
        {
            get => Check().Position;
            set => Check().Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = Check().Read(buffer, offset, count);
            _ownerCancellation.ThrowIfCancellationRequested();
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = Check().Read(buffer);
            _ownerCancellation.ThrowIfCancellationRequested();
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Stream inner = Check();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                _ownerCancellation, cancellationToken);
            int read = await inner.ReadAsync(buffer, linked.Token);
            Check();
            linked.Token.ThrowIfCancellationRequested();
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) =>
            Check().Seek(offset, origin);

        public override void Flush()
        {
            _ = Check();
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Interlocked.Exchange(ref _source, null);
            // The owner, not this wrapper, owns and closes the underlying
            // FileStream. Revoking view access must not close it early.
            base.Dispose(disposing);
        }
    }
}
