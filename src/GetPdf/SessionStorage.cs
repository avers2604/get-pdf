namespace GetPdf;

/// <summary>
/// Per-user scan/PDF sessions. A FileShare.None OS lease prevents a second
/// instance or startup cleanup from deleting an in-use session. Recovery
/// manifests and image snapshots survive abandoned OCR/print scratch cleanup.
/// Only exact GUID-named children of the known session root are eligible.
/// </summary>
internal static class SessionStorage
{
    internal static readonly string RootPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GET-PDF", "Sessions");

    private const string LeaseFile = ".owner.lock";
    private const string RetiredFile = ".expired";
    private static readonly object Gate = new();
    private static readonly Dictionary<string, FileStream> Owned =
        new(StringComparer.OrdinalIgnoreCase);

    internal sealed class SessionLease : IDisposable
    {
        private bool _disposed;
        public string Folder { get; }
        internal SessionLease(string folder) => Folder = folder;
        internal bool IsActive => !_disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseOwnership(Folder);
        }
    }

    public static SessionLease CreateLeased() => new(Create());
    internal static bool IsOwned(string folder)
    {
        lock (Gate) return Owned.ContainsKey(Path.GetFullPath(folder));
    }
    public static SessionLease? TryAcquire(string folder) =>
        TryClaim(folder) ? new SessionLease(Path.GetFullPath(folder)) : null;
    public static bool CanAcquire(string folder) => CanClaim(folder);
    public static void DeleteOwned(SessionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!lease.IsActive) return;
        TryDelete(lease.Folder);
        lease.Dispose();
    }

    public static string Create() => CreateIn(RootPath);

    internal static string CreateIn(string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(root);
        var folder = Path.Combine(root, "session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        if (!TryClaim(folder, root, allowNewSession: true))
            throw new IOException("Невозможно закрепить новую сессию GET PDF.");
        return folder;
    }

    public static bool TryClaim(
        string folder, string? rootOverride = null, bool allowNewSession = false)
    {
        var root = Path.GetFullPath(rootOverride ?? RootPath);
        if (!IsSession(folder, root)) return false;
        var path = Path.GetFullPath(folder);
        lock (Gate)
        {
            // Legacy folders have no owner lock. They may belong to an
            // older still-running application and must not be auto-claimed.
            if ((!allowNewSession && !File.Exists(Path.Combine(path, LeaseFile))) ||
                Owned.ContainsKey(path) ||
                File.Exists(Path.Combine(path, RetiredFile))) return false;
            try
            {
                var stream = new FileStream(Path.Combine(path, LeaseFile),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (File.Exists(Path.Combine(path, RetiredFile)))
                {
                    stream.Dispose();
                    return false;
                }
                Owned.Add(path, stream);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    internal static bool CanClaim(string folder)
    {
        var root = Path.GetFullPath(RootPath);
        if (!IsSession(folder, root)) return false;
        var path = Path.GetFullPath(folder);
        lock (Gate)
        {
            if (!File.Exists(Path.Combine(path, LeaseFile)) ||
                Owned.ContainsKey(path) ||
                File.Exists(Path.Combine(path, RetiredFile)))
                return false;
            try
            {
                using var lease = new FileStream(Path.Combine(path, LeaseFile),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public static void ReleaseOwnership(string folder)
    {
        var path = Path.GetFullPath(folder);
        lock (Gate)
        {
            if (Owned.Remove(path, out var stream)) stream.Dispose();
        }
    }

    internal static void ReleaseAllUnder(string root)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        lock (Gate)
        {
            foreach (var name in Owned.Keys.Where(name =>
                name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                if (Owned.Remove(name, out var stream)) stream.Dispose();
            }
        }
    }

    public static void CleanupExpired(
        TimeSpan age, string? protectedFolder = null, string? rootOverride = null)
    {
        if (age <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(age));
        var root = Path.GetFullPath(rootOverride ?? RootPath);
        if (!Directory.Exists(root)) return;
        var cutoff = DateTime.UtcNow - age;
        try
        {
            foreach (var session in Directory.EnumerateDirectories(root, "session-*",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (!IsSession(session, root) ||
                        // Never sweep legacy sessions without a known lease:
                        // the old GET PDF version may still own them.
                        !File.Exists(Path.Combine(session, LeaseFile)) ||
                        string.Equals(Path.GetFullPath(session), protectedFolder is null
                            ? null : Path.GetFullPath(protectedFolder),
                            StringComparison.OrdinalIgnoreCase)) continue;

                    var expired = File.Exists(Path.Combine(session, RetiredFile));
                    var recoverable = File.Exists(Path.Combine(session, "recovery.json")) ||
                        File.Exists(Path.Combine(session, "pdf-draft.json"));
                    // The lock probe may touch the session directory. A valid
                    // recovery can still contain much older abandoned scratch.
                    if (!expired && !recoverable &&
                        Directory.GetLastWriteTimeUtc(session) >= cutoff)
                        continue;

                    // Holding this lease prevents another app from using the
                    // session while we inspect or clean its OCR/print children.
                    using (var lease = TryTemporaryLease(session))
                    {
                        if (lease is null) continue;
                        if (!expired && recoverable)
                        {
                            CleanupOrphanScratch(session, cutoff);
                            continue;
                        }
                        if (ContainsReparsePoints(session))
                            continue;
                        // Deny new recovery claims after releasing the lease.
                        File.WriteAllText(Path.Combine(session, RetiredFile), string.Empty);
                    }
                    // No recoverable images and no active owner; Windows may
                    // require closing the lease before deleting its parent.
                    Directory.Delete(session, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                            ArgumentException)
                {
                    // Best effort. A later launch can retry marked sessions.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unavailable session storage must not prevent offline scanning.
        }
    }

    private static FileStream? TryTemporaryLease(string folder)
    {
        lock (Gate)
        {
            if (Owned.ContainsKey(Path.GetFullPath(folder))) return null;
            try
            {
                return new FileStream(Path.Combine(folder, LeaseFile),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private static void CleanupOrphanScratch(string session, DateTime cutoff)
    {
        foreach (var folder in Directory.EnumerateDirectories(session, "*",
                     SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(folder);
            var prefix = name.StartsWith("ocr-", StringComparison.Ordinal) ? "ocr-" :
                name.StartsWith("print-", StringComparison.Ordinal) ? "print-" : null;
            if (prefix is null || !Guid.TryParseExact(name[prefix.Length..], "N", out _) ||
                Directory.GetLastWriteTimeUtc(folder) >= cutoff ||
                ContainsReparsePoints(folder))
                continue;
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool ContainsReparsePoints(string folder)
    {
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            return true;
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
            if ((attributes & FileAttributes.Directory) != 0 &&
                ContainsReparsePoints(entry)) return true;
        }
        return false;
    }

    private static bool IsSession(string folder, string root)
    {
        try
        {
            var path = Path.GetFullPath(folder);
            var name = Path.GetFileName(path);
            return name.StartsWith("session-", StringComparison.Ordinal) &&
                Guid.TryParseExact(name["session-".Length..], "N", out _) &&
                string.Equals(Path.GetDirectoryName(path), root,
                    StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(path) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException)
        {
            return false;
        }
    }

    public static void TryDelete(string folder, string? rootOverride = null)
    {
        var root = Path.GetFullPath(rootOverride ?? RootPath);
        if (!IsSession(folder, root)) return;
        var path = Path.GetFullPath(folder);
        lock (Gate)
        {
            // Never delete a folder that this instance has not claimed.
            if (!Owned.TryGetValue(path, out var stream)) return;
            try { File.WriteAllText(Path.Combine(path, RetiredFile), string.Empty); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            Owned.Remove(path);
            stream.Dispose();
        }
        try
        {
            if (!ContainsReparsePoints(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
