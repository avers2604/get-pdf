using System.Collections.Concurrent;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27bm: cooperative, same-host capture serialization and conservative
/// volume-space admission. NOT a secure cross-host SMB lease or a reservation
/// against independent processes. Lock files are persistent and never deleted.
/// </summary>
internal static class RecoveryCaptureVolumeGate
{
    internal const string LockFileName = ".getdoc-capture.lock";
    internal const long MaximumOfficeCopyBytes = 128L * 1024 * 1024;
    internal const long MinimumFreeBytes = 512L * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> LocalGates =
        new(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal static async Task<RecoveryCaptureVolumeLease> AcquireAsync(
        string recoveryRoot,
        Func<string, RecoveryCaptureVolumeObservation>? observeVolume = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryRoot);
        cancellationToken.ThrowIfCancellationRequested();

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(recoveryRoot));
        RequirePlainDirectoryAncestors(root); // before opening/creating anything
        if (LockingDisabledByRuntime())
            throw new PlatformNotSupportedException(
                "Среда отключила файловые блокировки: создание копии запрещено.");

        Func<string, RecoveryCaptureVolumeObservation> probe =
            observeVolume ?? ObserveRealVolume;
        RequireAvailableLocalSpace(probe(root));

        // Lock file is in the dedicated recovery root, NOT in any user's
        // source directory or the per-document recovery folder. It is never
        // removed, even after cancellation or a crash.
        Directory.CreateDirectory(root);
        RequirePlainDirectoryAncestors(root);
        var local = LocalGates.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
        if (!await local.WaitAsync(0, cancellationToken))
            throw new IOException(
                "Другой сеанс уже создаёт аварийный снимок в этом хранилище.");

        FileStream? handle = null;
        bool locked = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string lockPath = Path.Combine(root, LockFileName);
            RequirePlainLockFileIfPresent(lockPath);
            // OpenOrCreate leaves a stable, EMPTY coordination inode in place.
            // No path cleanup: deleting a lock pathname would permit separate
            // processes to coordinate on DIFFERENT inodes after a rename.
            handle = new FileStream(lockPath, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.ReadWrite,
                bufferSize: 1, options: FileOptions.None);
            RequirePlainDirectoryAncestors(root);
            RequirePlainLockFileIfPresent(lockPath);
            if (handle.Length != 0)
                throw new InvalidDataException(
                    "Файл координации аварийных снимков имеет неожиданные данные.");

            // Same-host cooperative byte-range lock, held across all awaits
            // until the owner's finally/Dispose. On Unix this is advisory.
            // A cooperating second process must fail closed on contention.
            handle.Lock(0, 1);
            locked = true;
            cancellationToken.ThrowIfCancellationRequested();
            // Reobserve under the lock to avoid starting on a stale reading.
            RequireAvailableLocalSpace(probe(root));
            cancellationToken.ThrowIfCancellationRequested();

            var lease = new RecoveryCaptureVolumeLease(handle, local);
            handle = null;
            return lease;
        }
        catch
        {
            if (locked && handle is not null)
            {
                try { handle.Unlock(0, 1); }
                catch (IOException) { } // preserve the original failure
            }
            handle?.Dispose();
            local.Release();
            throw;
        }
    }

    private static bool LockingDisabledByRuntime()
    {
        if (AppContext.TryGetSwitch("System.IO.DisableFileLocking", out bool off) && off)
            return true;
        string? setting = Environment.GetEnvironmentVariable(
            "DOTNET_SYSTEM_IO_DISABLEFILELOCKING");
        return setting is not null &&
            (setting == "1" || setting.Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    private static void RequireAvailableLocalSpace(RecoveryCaptureVolumeObservation observed)
    {
        if (!observed.IsApprovedLocalVolume)
            throw new PlatformNotSupportedException(
                "Блокировка файлов на сетевом или неподтверждённом томе не аттестована.");
        if (observed.AvailableFreeBytes < MinimumFreeBytes)
            throw new IOException(
                "Недостаточно свободного места для аварийной копии и резерва диска.");
    }

    private static RecoveryCaptureVolumeObservation ObserveRealVolume(string root)
    {
        // Longest mounted path wins. On Linux this can distinguish an ext4
        // mount from a nested CIFS/NFS mount; don't mistake '/' for the latter.
        string path = Path.GetFullPath(root);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        DriveInfo? drive = DriveInfo.GetDrives()
            .Where(candidate =>
            {
                string mounted = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(candidate.Name));
                return path.Equals(mounted, comparison) ||
                    path.StartsWith(mounted.EndsInDirectorySeparator()
                        ? mounted : mounted + Path.DirectorySeparatorChar, comparison);
            })
            .OrderByDescending(candidate => candidate.Name.Length)
            .FirstOrDefault();
        if (drive is null || !drive.IsReady)
            throw new IOException("Том аварийного восстановления недоступен.");

        string kind = drive.DriveFormat;
        bool local =
            (OperatingSystem.IsWindows() && drive.DriveType == DriveType.Fixed &&
             (kind.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ||
              kind.Equals("ReFS", StringComparison.OrdinalIgnoreCase))) ||
            (OperatingSystem.IsLinux() && drive.DriveType != DriveType.Network &&
             kind is "ext4" or "xfs" or "btrfs" or "overlay" or "tmpfs");

        // CIFS, SMB, NFS, unknown mount types and Windows UNC mappings are
        // rejected. No cross-host lease is claimed by this implementation.
        return new RecoveryCaptureVolumeObservation(
            local, drive.AvailableFreeSpace, kind);
    }

    private static void RequirePlainDirectoryAncestors(string root)
    {
        for (DirectoryInfo? current = new(root); current is not null;
             current = current.Parent)
        {
            current.Refresh();
            if (current.LinkTarget is not null ||
                (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new InvalidDataException(
                    "Путь координации аварийного восстановления содержит ссылку.");
        }
    }

    private static void RequirePlainLockFileIfPresent(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        if (file.LinkTarget is not null ||
            (file.Exists && (file.Attributes &
                (FileAttributes.ReparsePoint | FileAttributes.Directory |
                 FileAttributes.Device)) != 0))
            throw new InvalidDataException(
                "Файл координации не является обычным файлом.");
    }
}

/// <summary>
/// Fake observations are accepted by the internal test seam only. Production
/// reads the live mounted volume. This is NOT an actual byte reservation.
/// </summary>
internal sealed record RecoveryCaptureVolumeObservation(
    bool IsApprovedLocalVolume, long AvailableFreeBytes, string FileSystemType);

internal sealed class RecoveryCaptureVolumeLease : IDisposable
{
    private FileStream? _handle;
    private readonly SemaphoreSlim _local;
    private int _disposed;

    internal RecoveryCaptureVolumeLease(FileStream handle, SemaphoreSlim local)
    {
        _handle = handle;
        _local = local;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        FileStream? handle = Interlocked.Exchange(ref _handle, null);
        try
        {
            if (handle is not null)
            {
                try { handle.Unlock(0, 1); }
                catch (IOException) { } // dispose still releases the OS handle
            }
        }
        finally
        {
            try { handle?.Dispose(); }
            finally { _local.Release(); }
        }
    }
}
