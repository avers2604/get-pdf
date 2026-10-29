using System.Security.Cryptography;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// Detects modifications made to a local/SMB Office file after it was opened.
/// Only a cooperative optimistic-concurrency check: it is NOT an atomic SMB CAS,
/// WORM authorization, file lock, or guarantee against changes during UNO store().
/// </summary>
public sealed class OfficeFileRevisionGuard
{
    private const long MaximumSnapshotBytes = 256L * 1024 * 1024;
    private readonly string _path;
    private FileRevision _accepted;

    private OfficeFileRevisionGuard(string path, FileRevision accepted)
    {
        _path = path;
        _accepted = accepted;
    }

    public static async Task<OfficeFileRevisionGuard> CreateAsync(
        string path, CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(path);
        return new OfficeFileRevisionGuard(fullPath,
            await ReadRevisionAsync(fullPath, cancellationToken));
    }

    /// <summary>
    /// A changed or missing file MUST NOT be overwritten by an automatic save.
    /// The in-memory editor stays open so the user can export a separate copy.
    /// </summary>
    public async Task EnsureUnchangedAsync(CancellationToken cancellationToken = default)
    {
        FileRevision current;
        try
        {
            current = await ReadRevisionAsync(_path, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            throw new OfficeFileConflictException();
        }
        catch (DirectoryNotFoundException)
        {
            throw new OfficeFileConflictException();
        }
        if (current != _accepted)
            throw new OfficeFileConflictException();
    }

    /// <summary>
    /// Call ONLY after a successful engine save. Never refresh the baseline on
    /// an error or a rejected save; that would silently accept a colleague's edit.
    /// </summary>
    public async Task AcceptSavedAsync(CancellationToken cancellationToken = default)
    {
        _accepted = await ReadRevisionAsync(_path, cancellationToken);
    }

    private static async Task<FileRevision> ReadRevisionAsync(
        string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 81920, useAsync: true);
        long beforeLength = stream.Length;
        DateTime beforeModified = File.GetLastWriteTimeUtc(path);
        if (beforeLength > MaximumSnapshotBytes)
            throw new InvalidDataException("Файл превышает лимит безопасной проверки версии (256 МБ).");
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        // Reject a torn snapshot if a writer changed the source during hashing.
        if (stream.Length != beforeLength || File.GetLastWriteTimeUtc(path) != beforeModified)
            throw new OfficeFileConflictException();
        return new FileRevision(beforeLength, beforeModified, Convert.ToHexString(hash));
    }

    private sealed record FileRevision(long Length, DateTime ModifiedUtc, string Sha256);
}

/// <summary>
/// Serializes saves for one document and runs its revision check BEFORE any
/// write callback. A store() failure leaves the old baseline unchanged.
/// This does not make remote SMB writes atomic: server CAS is a separate gate.
/// </summary>
public sealed class OfficeRevisionSaveCoordinator
{
    private readonly OfficeFileRevisionGuard _guard;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OfficeRevisionSaveCoordinator(OfficeFileRevisionGuard guard)
    {
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
    }

    public async Task SaveAsync(Func<Task> nativeStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nativeStore);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _guard.EnsureUnchangedAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await nativeStore();
            // A native store can return before its new snapshot is readable;
            // failure here must NOT be mistaken for a successful verified save.
            await _guard.AcceptSavedAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }
}

/// <summary>Recoverable save conflict: retain edits and offer a separate copy.</summary>
public sealed class OfficeFileConflictException : IOException
{
    public OfficeFileConflictException()
        : base("Исходный файл изменён или удалён другим процессом. " +
               "Перезапись отменена. Сохраните текущие изменения отдельной копией.")
    {
    }
}
