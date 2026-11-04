using System.Security.Cryptography;
using System.Text;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27bo: best-effort cooperative, same-machine cross-process gate
/// for writers with different recovery roots on the same selected local mount.
/// Not an OS-native identity-bound lock, not distributed, not a byte quota.
/// Lock names and directory must remain persistent; NEVER delete them.
/// </summary>
internal static class RecoverySharedVolumeCoordinator
{
    private const string LockPrefix = ".getdoc-volume-";
    private const string LockSuffix = ".lock";

    internal static string GetLockPathForPlanning(string coordinatorRoot, string volumeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coordinatorRoot);
        if (!Path.IsPathFullyQualified(coordinatorRoot))
            throw new ArgumentException("Требуется абсолютный каталог координатора.",
                nameof(coordinatorRoot));
        if (string.IsNullOrWhiteSpace(volumeKey) || volumeKey.Length > 2048)
            throw new ArgumentException("Неизвестен идентификатор локального тома.",
                nameof(volumeKey));

        // A mount/drive spelling, NOT a native volume serial. Hash hides
        // path characters and guarantees a single safe filename grammar.
        string normalized = OperatingSystem.IsWindows()
            ? volumeKey.ToUpperInvariant() : volumeKey;
        string digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(Path.GetFullPath(coordinatorRoot),
            LockPrefix + digest + LockSuffix);
    }

    internal static string DefaultCoordinatorRoot()
    {
        string parent = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(parent) || !Path.IsPathFullyQualified(parent))
            throw new PlatformNotSupportedException(
                "Локальный каталог приложения для межпроцессной координации недоступен.");

        return Path.Combine(parent, "GET-Document", "recovery-volume-coordinator");
    }

    internal static Task<RecoverySharedVolumeLease> AcquireAsync(
        string volumeKey,
        string? coordinatorRoot = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(coordinatorRoot ?? DefaultCoordinatorRoot());
        string lockPath = GetLockPathForPlanning(root, volumeKey);
        if (coordinatorRoot is null)
        {
            // The real local coordinator itself must be on a recognized
            // LOCAL volume. A redirected AppData folder or network mount
            // is not a supported local cross-process lock backend.
            RecoveryCaptureVolumeObservation location =
                RecoveryCaptureVolumeGate.ObserveRealVolume(root);
            if (!location.IsApprovedLocalVolume)
                throw new PlatformNotSupportedException(
                    "Каталог координатора расположен на неподтверждённом или сетевом томе.");
        }
        RequirePlainDirectoryTree(root);
        cancellationToken.ThrowIfCancellationRequested();

        // The directory is app-local, not a user document/recovery source.
        // Any path change (symlink/junction/reparse) seen at a checkpoint
        // fails closed, but this DOES NOT close the hostile rename TOCTOU.
        // Create new Linux coordinator directories as 0700, never apply chmod
        // to an existing path that may belong to somebody else. Existing
        // loose directories must be inspected/migrated out of band.
        const UnixFileMode OwnerOnly = UnixFileMode.UserRead |
            UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (OperatingSystem.IsLinux())
            Directory.CreateDirectory(root, OwnerOnly);
        else
            Directory.CreateDirectory(root);
        RequirePlainDirectoryTree(root);

        if (OperatingSystem.IsLinux())
        {
            UnixFileMode mode = Directory.GetUnixFileMode(root);
            if ((mode & OwnerOnly) != OwnerOnly ||
                (mode & ~OwnerOnly) != (UnixFileMode)0)
                throw new InvalidDataException(
                    "Каталог координатора должен иметь приватные права 0700 " +
                    "и не исправляется автоматически.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        RequirePlainLockFileIfPresent(lockPath);

        FileStream? handle = null;
        bool locked = false;
        try
        {
            handle = new FileStream(lockPath, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.ReadWrite,
                bufferSize: 1, options: FileOptions.None);
            RequirePlainDirectoryTree(root);
            RequirePlainLockFileIfPresent(lockPath);
            if (handle.Length != 0)
                throw new InvalidDataException(
                    "Файл межпроцессной координации имеет посторонние данные.");

            // Byte-range lock is cooperative/advisory on Unix. The platform
            // must prove its cross-process semantics before release.
            handle.Lock(0, 1);
            locked = true;
            RequirePlainLockFileIfPresent(lockPath);
            if (!File.Exists(lockPath))
                throw new InvalidDataException(
                    "Файл координации исчез после захвата блокировки.");
            cancellationToken.ThrowIfCancellationRequested();

            var lease = new RecoverySharedVolumeLease(handle);
            handle = null;
            return Task.FromResult(lease);
        }
        catch
        {
            if (locked && handle is not null)
            {
                try { handle.Unlock(0, 1); }
                catch (IOException) { } // keep original failure
            }
            handle?.Dispose();
            throw;
        }
    }

    private static void RequirePlainDirectoryTree(string root)
    {
        for (DirectoryInfo? part = new(root); part is not null;
             part = part.Parent)
        {
            part.Refresh();
            if (part.LinkTarget is not null ||
                (part.Exists && (part.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new InvalidDataException(
                    "Каталог межпроцессного координатора содержит ссылку.");
        }
    }

    private static void RequirePlainLockFileIfPresent(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        if (file.LinkTarget is not null ||
            (file.Exists && (file.Attributes & (FileAttributes.ReparsePoint |
                 FileAttributes.Directory | FileAttributes.Device)) != 0))
            throw new InvalidDataException(
                "Файл межпроцессной координации подменён ссылкой или не является обычным.");
    }
}

/// <summary>
/// Holds the shared per-volume OS lock until the capture's last finally.
/// After process termination, OS normally releases a kernel lock; a stale
/// EMPTY pathname is not deleted and is reused on the next capture.
/// </summary>
internal sealed class RecoverySharedVolumeLease : IDisposable
{
    private FileStream? _handle;

    internal RecoverySharedVolumeLease(FileStream handle) =>
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));

    public void Dispose()
    {
        FileStream? handle = Interlocked.Exchange(ref _handle, null);
        if (handle is null) return;
        try
        {
            try { handle.Unlock(0, 1); }
            catch (IOException) { } // dispose still closes the OS handle
        }
        finally { handle.Dispose(); }
    }
}
