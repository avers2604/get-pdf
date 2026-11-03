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
            var view = new RevocableReadOnlyStream(source, abort);
            try
            {
                T result = await reader(view, abort);
                // Callbacks may start a ReadAsync without awaiting it. Revoke
                // the view AND drain all already-started I/O before performing
                // the final same-handle SHA/ZIP verification or closing it.
                await view.RevokeAndDrainAsync();
                abort.ThrowIfCancellationRequested();

                source.Position = 0;
                if (!OfficeRecoveryStore.VerifyOpenedPackage(
                        source, Receipt.Extension, Receipt.Sha256))
                    throw new InvalidDataException(
                        "Приёмный файл изменился во время чтения.");
                abort.ThrowIfCancellationRequested();

                if (Interlocked.CompareExchange(ref _state, 2, 1) != 1)
                    throw new ObjectDisposedException(nameof(RecoveryOwnedConsumerOutput));

                // A result from an already finished callback is an observation,
                // not authorization for any WORM/document publication.
                return result;
            }
            finally
            {
                // Also drain in exceptional/cancelled callback paths.
                await view.RevokeAndDrainAsync();
            }
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
    // Internal for isolated synthetic concurrency regressions only.
    // The view never owns the FileStream; the owning output closes it.
    internal sealed class RevocableReadOnlyStream : Stream
    {
        private readonly object _gate = new();
        private readonly CancellationToken _ownerCancellation;
        private readonly CancellationTokenSource _revocationCancellation = new();
        private Stream? _source;
        private TaskCompletionSource<bool>? _drain;
        private bool _revoked;
        private bool _active;

        internal RevocableReadOnlyStream(Stream source, CancellationToken ownerCancellation)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _ownerCancellation = ownerCancellation;
        }

        private Stream Begin()
        {
            lock (_gate)
            {
                if (_revoked || _source is null)
                    throw new ObjectDisposedException(nameof(RevocableReadOnlyStream));
                _ownerCancellation.ThrowIfCancellationRequested();
                if (_active)
                    throw new InvalidOperationException(
                        "Одновременные обращения к одному проверенному потоку запрещены.");
                _active = true;
                return _source;
            }
        }

        private void End()
        {
            TaskCompletionSource<bool>? drain = null;
            lock (_gate)
            {
                _active = false;
                drain = _drain;
            }
            drain?.TrySetResult(true);
        }

        private void RequireNotRevoked()
        {
            if (Volatile.Read(ref _source) is null)
                throw new ObjectDisposedException(nameof(RevocableReadOnlyStream));
            _ownerCancellation.ThrowIfCancellationRequested();
        }

        internal async Task RevokeAndDrainAsync()
        {
            Dispose();
            Task drain;
            lock (_gate)
            {
                drain = !_active ? Task.CompletedTask :
                    (_drain ??= new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            await drain;
            _revocationCancellation.Dispose();
        }

        public override bool CanRead => Volatile.Read(ref _source)?.CanRead == true;
        public override bool CanSeek => Volatile.Read(ref _source)?.CanSeek == true;
        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                Stream source = Begin();
                try { return source.Length; }
                finally { End(); }
            }
        }

        public override long Position
        {
            get
            {
                Stream source = Begin();
                try { return source.Position; }
                finally { End(); }
            }
            set
            {
                Stream source = Begin();
                try { source.Position = value; }
                finally { End(); }
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Stream source = Begin();
            try
            {
                int countRead = source.Read(buffer, offset, count);
                RequireNotRevoked();
                return countRead;
            }
            finally { End(); }
        }

        public override int Read(Span<byte> buffer)
        {
            Stream source = Begin();
            try
            {
                int countRead = source.Read(buffer);
                RequireNotRevoked();
                return countRead;
            }
            finally { End(); }
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Stream source = Begin();
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    _ownerCancellation, _revocationCancellation.Token, cancellationToken);
                int countRead = await source.ReadAsync(buffer, linked.Token);
                RequireNotRevoked();
                linked.Token.ThrowIfCancellationRequested();
                return countRead;
            }
            finally { End(); }
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin)
        {
            Stream source = Begin();
            try { return source.Seek(offset, origin); }
            finally { End(); }
        }

        public override void Flush()
        {
            _ = Begin();
            try { RequireNotRevoked(); }
            finally { End(); }
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            bool cancel = false;
            if (disposing)
            {
                lock (_gate)
                {
                    if (!_revoked)
                    {
                        _revoked = true;
                        Interlocked.Exchange(ref _source, null);
                        cancel = true;
                    }
                }
                if (cancel)
                    _revocationCancellation.Cancel();
            }
            base.Dispose(disposing);
        }
    }
}
