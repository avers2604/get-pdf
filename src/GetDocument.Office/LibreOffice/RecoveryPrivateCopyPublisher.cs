namespace GetDocument.Office.LibreOffice;

/// <summary>
/// A disposable, privately-staged OOXML copy. This is NOT a restored document,
/// a WORM revision or permission to reopen FilePath for trusted consumption.
/// The verified FileStream stays owned by this object until disposal.
/// </summary>
internal sealed class RecoveryPrivatePublication : IDisposable
{
    private FileStream? _heldStream;
    private readonly string _privateDirectory;

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

    public void Dispose()
    {
        FileStream? handle = Interlocked.Exchange(ref _heldStream, null);
        if (handle is null) return;
        handle.Dispose();
        // Delete ONLY the exact file allocated in our unique private
        // directory. Never recursively remove a directory containing foreign
        // entries. This remains path-based; hostile local rename races require
        // native handle-bound cleanup on an approved OS test rig.
        RecoveryPrivateCopyPublisher.TryDeleteOwnRegularFile(
            _privateDirectory, FilePath);
        RecoveryPrivateCopyPublisher.TryRemoveEmptyPrivateDirectory(
            _privateDirectory);
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

    internal static void TryDeleteOwnRegularFile(string privateDirectory, string filePath)
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
