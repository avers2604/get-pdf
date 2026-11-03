namespace GetDocument.Office.LibreOffice;

/// <summary>
/// A disposable, privately-staged OOXML copy. This is NOT a restored document,
/// a WORM revision or permission to reopen FilePath for trusted consumption.
/// The verified FileStream stays owned by this object until disposal.
/// </summary>
internal sealed class RecoveryPrivatePublication : IDisposable
{
    private const int TransferBufferBytes = 64 * 1024;
    private const long MaxCopyBytes = 128L * 1024 * 1024;

    private FileStream? _heldStream;
    private readonly string _privateDirectory;
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    // 0 = ready, 1 = transferring, 2 = consumed, 3 = disposed.
    // Closing during an active transfer cancels it; cleanup runs only
    // AFTER the consumer has stopped using the held file descriptor.
    private int _deliveryState;
    private int _cleanupStarted;

    internal string FilePath { get; }
    internal RecoveryStagedCopyReceipt Receipt { get; }

    internal RecoveryPrivatePublication(
        string privateDirectory, string filePath, FileStream heldStream,
        RecoveryStagedCopyReceipt receipt)
    {
        _privateDirectory = privateDirectory;
        FilePath = filePath;
        _heldStream = heldStream;
        Receipt = receipt;
    }

    /// <summary>
    /// Transfer the verified bytes to a NEW caller-owned output stream,
    /// keeping the original publication handle open until the operation is
    /// finished or cancelled. The source file is NEVER reopened by pathname.
    /// The returned receipt is not permission to commit a WORM revision.
    /// </summary>
    internal async Task<RecoveryStagedCopyReceipt> ConsumeVerifiedToAsync(
        Stream destination, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _deliveryState) == 3)
            throw new ObjectDisposedException(nameof(RecoveryPrivatePublication));
        if (Interlocked.CompareExchange(ref _deliveryState, 1, 0) != 0)
            throw new InvalidOperationException(
                "Опубликованная временная копия может быть передана только один раз.");

        bool ownedEmptyDestination = false;
        try
        {
            ArgumentNullException.ThrowIfNull(destination);
            if (!destination.CanRead || !destination.CanWrite ||
                !destination.CanSeek || destination.Length != 0 ||
                destination.Position != 0)
                throw new ArgumentException(
                    "Приёмник должен быть новым пустым читаемым и записываемым потоком.",
                    nameof(destination));

            // This flag is set only AFTER checking that the caller-provided
            // stream is empty. Never truncate a preexisting consumer file.
            ownedEmptyDestination = true;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetimeCancellation.Token);
            CancellationToken abort = linked.Token;
            abort.ThrowIfCancellationRequested();

            FileStream source = _heldStream
                ?? throw new ObjectDisposedException(nameof(RecoveryPrivatePublication));
            if (!source.CanRead || !source.CanSeek ||
                source.Length != Receipt.Length ||
                source.Length == 0 || source.Length > MaxCopyBytes)
                throw new InvalidDataException(
                    "Состояние открытой временной копии не соответствует квитанции.");

            // Fresh SHA-256 and bounded ZIP/XML on the SAME held descriptor
            // before the first byte is copied to the consumer.
            source.Position = 0;
            if (!OfficeRecoveryStore.VerifyOpenedPackage(
                    source, Receipt.Extension, Receipt.Sha256))
                throw new InvalidDataException(
                    "Открытая временная копия не прошла повторную проверку.");
            abort.ThrowIfCancellationRequested();

            source.Position = 0;
            using var hasher = System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);
            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(TransferBufferBytes);
            long transferred = 0;
            try
            {
                while (true)
                {
                    abort.ThrowIfCancellationRequested();
                    int limit = (int)Math.Min(buffer.Length,
                        MaxCopyBytes - transferred + 1);
                    int bytes = await source.ReadAsync(
                        buffer.AsMemory(0, limit), abort);
                    if (bytes == 0) break;

                    transferred += bytes;
                    if (transferred > MaxCopyBytes)
                        throw new InvalidDataException(
                            "Размер аварийной копии превысил лимит 128 МиБ.");
                    await destination.WriteAsync(
                        buffer.AsMemory(0, bytes), abort);
                    hasher.AppendData(buffer, 0, bytes);
                    abort.ThrowIfCancellationRequested();
                }

                if (transferred != Receipt.Length)
                    throw new InvalidDataException(
                        "Количество переданных байтов не соответствует квитанции.");
                await destination.FlushAsync(abort);
                abort.ThrowIfCancellationRequested();

                string digest = Convert.ToHexString(hasher.GetHashAndReset());
                if (!string.Equals(digest, Receipt.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "SHA-256 фактически переданных байтов не совпал с квитанцией.");

                // Independent verification of the exact consumer-owned bytes.
                // Output was checked empty before writing; an invalid copy
                // is cleared best effort and NEVER yields a receipt.
                if (destination.Length != Receipt.Length)
                    throw new InvalidDataException(
                        "Размер файла у потребителя изменился после передачи.");
                destination.Position = 0;
                if (!OfficeRecoveryStore.VerifyOpenedPackage(
                        destination, Receipt.Extension, digest))
                    throw new InvalidDataException(
                        "Контрольная SHA/ZIP-проверка потребителя не пройдена.");

                destination.Position = 0;
                abort.ThrowIfCancellationRequested();
                if (Interlocked.CompareExchange(ref _deliveryState, 2, 1) != 1)
                    throw new ObjectDisposedException(nameof(RecoveryPrivatePublication));

                // The consumer now owns destination. This success is
                // linearized BEFORE a later independent Dispose call.
                return new RecoveryStagedCopyReceipt(
                    transferred, digest, Receipt.Extension);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(
                    buffer, clearArray: true);
            }
        }
        catch
        {
            if (ownedEmptyDestination)
                TryClearConsumerDestination(destination);
            throw;
        }
        finally
        {
            // All source reads have completed/aborted. Disposal while the
            // transfer was active only cancelled it; actual file cleanup
            // is deferred until this final release.
            CloseAndCleanup();
        }
    }

    private static void TryClearConsumerDestination(Stream destination)
    {
        try
        {
            if (destination.CanSeek && destination.CanWrite)
            {
                destination.SetLength(0);
                destination.Position = 0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or NotSupportedException or ObjectDisposedException
                                   or System.Security.SecurityException)
        {
            // Never report success. Caller must discard the untrusted sink if
            // the best-effort cleanup could not complete.
        }
    }

    public void Dispose()
    {
        int previous = Interlocked.Exchange(ref _deliveryState, 3);
        if (previous == 3) return;
        try
        {
            _lifetimeCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A consumer may have finished and already released the CTS.
        }

        if (previous != 1)
            CloseAndCleanup(); // active transfer closes itself in finally
    }

    private void CloseAndCleanup()
    {
        if (Interlocked.Exchange(ref _cleanupStarted, 1) != 0) return;
        FileStream? handle = Interlocked.Exchange(ref _heldStream, null);
        try
        {
            handle?.Dispose();
        }
        finally
        {
            _lifetimeCancellation.Dispose();
            // Delete ONLY if the current path still matches the verified
            // content, reducing accidental deletion after simple rename
            // swaps. This remains PATH-BASED and cannot eliminate TOCTOU.
            RecoveryPrivateCopyPublisher.TryDeleteOwnRegularFile(
                _privateDirectory, FilePath, Receipt);
            RecoveryPrivateCopyPublisher.TryRemoveEmptyPrivateDirectory(
                _privateDirectory);
        }
    }
}

/// <summary>
/// GD-31J.27bg — isolated, UNREGISTERED staging owner and atomic final-name
/// publication in an OS-generated private temporary directory. No user-supplied
/// destination path or existing user file is ever accepted.
/// </summary>
internal static class RecoveryPrivateCopyPublisher
{
    internal static async Task<RecoveryPrivatePublication> PublishAsync(
        RecoveryVerifiedStreamStage session,
        string expectedSha256,
        string extension,
        CancellationToken cancellationToken = default,
        Action<string>? onPrivateDirectoryCreated = null,
        Action<string>? beforeRename = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        // Ownership of the source session passes into this method. It will be
        // closed even if validation, temp creation, or publishing fails.
        using (session)
        {
            if (extension is not (".docx" or ".xlsx" or ".pptx"))
                throw new ArgumentException("Недопустимый формат временной копии.",
                    nameof(extension));
            if (expectedSha256 is null || expectedSha256.Length != 64 ||
                !expectedSha256.All(Uri.IsHexDigit))
                throw new ArgumentException("Требуется SHA-256 опубликованной копии.",
                    nameof(expectedSha256));
            cancellationToken.ThrowIfCancellationRequested();

            // System API chooses a new unpredictable private directory.
            // Never accept a caller-supplied output directory or file name.
            DirectoryInfo privateDirectory =
                Directory.CreateTempSubdirectory("getdoc-recovery-private-");
            string root = privateDirectory.FullName;
            string name = Guid.NewGuid().ToString("N");
            string stagingPath = Path.Combine(root, name + ".part" + extension);
            string destination = Path.Combine(root, name + extension);
            bool createdStage = false;
            bool renamedStage = false;
            FileStream? staging = null;
            try
            {
                RejectLinkedDirectoryTree(root);
                onPrivateDirectoryCreated?.Invoke(root); // internal test seam
                RejectLinkedDirectoryTree(root);
                cancellationToken.ThrowIfCancellationRequested();

                staging = new FileStream(stagingPath, FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.Delete,
                    bufferSize: 65536, options: FileOptions.Asynchronous);
                createdStage = true;
                RecoveryStagedCopyReceipt receipt =
                    await session.StageToAsync(staging, expectedSha256,
                        extension, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                // Ensure file contents have been flushed before attempting
                // same-directory rename. This alone is not a power-loss
                // durability guarantee on every filesystem or SMB server.
                staging.Flush(flushToDisk: true);
                RejectLinkedDirectoryTree(root);
                RequireOwnedRegularStaging(stagingPath, staging, receipt);

                staging.Position = 0;
                if (!OfficeRecoveryStore.VerifyOpenedPackage(
                        staging, extension, receipt.Sha256))
                    throw new InvalidDataException(
                        "Временная OOXML-копия изменилась перед публикацией.");

                beforeRename?.Invoke(destination); // internal test seam
                cancellationToken.ThrowIfCancellationRequested();
                RejectLinkedDirectoryTree(root);
                RequireOwnedRegularStaging(stagingPath, staging, receipt);

                // File.Move(..., overwrite:false) is the only publication
                // operation. Source + destination are in the SAME private
                // directory; there is no attempt to replace user documents.
                // Keep the SAME handle open across rename (FileShare.Delete
                // permits normal Windows renames, subject to filesystem).
                File.Move(stagingPath, destination, overwrite: false);
                renamedStage = true;

                staging.Position = 0;
                if (!OfficeRecoveryStore.VerifyOpenedPackage(
                        staging, extension, receipt.Sha256))
                    throw new InvalidDataException(
                        "OOXML-копия изменилась при публикации.");

                // FilePath is informational; the owner retains the stream.
                var published = new RecoveryPrivatePublication(
                    root, destination, staging, receipt);
                staging = null; // owner now controls the held descriptor
                return published;
            }
            catch
            {
                staging?.Dispose();
                // Never delete a PRE-EXISTING destination on a collision.
                // Only clean a successfully-created stage or a successfully
                // renamed file in the private workspace. Refuse symlinks.
                if (renamedStage)
                    TryDeleteOwnRegularFile(root, destination);
                else if (createdStage)
                    TryDeleteOwnRegularFile(root, stagingPath);
                TryRemoveEmptyPrivateDirectory(root);
                throw;
            }
        }
    }

    private static void RequireOwnedRegularStaging(
        string stagingPath, FileStream opened, RecoveryStagedCopyReceipt receipt)
    {
        var file = new FileInfo(stagingPath);
        file.Refresh();
        if (!file.Exists || file.LinkTarget is not null ||
            (file.Attributes & (FileAttributes.Directory |
                                FileAttributes.ReparsePoint |
                                FileAttributes.Device)) != 0 ||
            file.Length != receipt.Length ||
            opened.Length != receipt.Length)
            throw new InvalidDataException(
                "Временный файл больше не соответствует проверенному потоку.");
    }

    private static void RejectLinkedDirectoryTree(string directory)
    {
        for (DirectoryInfo? current = new(directory);
             current is not null; current = current.Parent)
        {
            current.Refresh();
            if (current.LinkTarget is not null ||
                !current.Exists ||
                (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    "Личный временный каталог недоступен или содержит ссылку.");
        }
    }

    internal static void TryDeleteOwnRegularFile(
        string privateDirectory, string filePath,
        RecoveryStagedCopyReceipt? expected = null)
    {
        try
        {
            // Only an immediate child of our generated private directory.
            string parent = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
            if (!string.Equals(
                    parent, Path.GetFullPath(privateDirectory),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
                return;
            RejectLinkedDirectoryTree(privateDirectory);
            var info = new FileInfo(filePath);
            info.Refresh();
            if (info.LinkTarget is not null || !info.Exists ||
                (info.Attributes & (FileAttributes.ReparsePoint |
                                    FileAttributes.Directory |
                                    FileAttributes.Device)) != 0)
                return;

            // When an owned publication is released, the filename may have
            // been replaced by a foreign file. Never delete a different
            // length or SHA/ZIP payload just because it uses our old name.
            // Still not an atomic File ID comparison: native cleanup is
            // needed before this can run in a hostile production workspace.
            if (expected is not null)
            {
                if (info.Length != expected.Length) return;
                using (var check = new FileStream(filePath, FileMode.Open,
                    FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    if (check.Length != expected.Length ||
                        !OfficeRecoveryStore.VerifyOpenedPackage(
                            check, expected.Extension, expected.Sha256))
                        return;
                }
            }

            File.Delete(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException
                                   or InvalidDataException or ArgumentException)
        {
            // Fail closed: do not delete other entries or recursively clean.
            // A stranded private file requires an approved manual cleanup.
        }
    }

    internal static void TryRemoveEmptyPrivateDirectory(string directory)
    {
        try
        {
            RejectLinkedDirectoryTree(directory);
            Directory.Delete(directory, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException
                                   or InvalidDataException or DirectoryNotFoundException)
        {
            // Preserve unknown/foreign files. No recursive delete.
        }
    }
}
