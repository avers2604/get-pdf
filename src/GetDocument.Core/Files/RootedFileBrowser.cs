using GetDocument.Core.DocumentFiles;

namespace GetDocument.Core.Files;

/// <summary>Metadata only. The caller must recheck file existence and access before opening.</summary>
public sealed record SharedFileEntry(string Name, string FullPath, bool IsDirectory, bool CanOpen);

public sealed record SharedFolderSnapshot(
    string RootPath,
    string FolderPath,
    IReadOnlyList<SharedFileEntry> Entries,
    bool IsTruncated);

/// <summary>
/// One-level, bounded file browser over existing OS-mounted storage or UNC/SMB shares.
/// Uses the current process identity and operating system ACLs; no credentials stored.
/// It is not a security sandbox: the OS/server remains the authority for permissions.
/// </summary>
public sealed class RootedFileBrowser
{
    public const int DefaultMaxEntries = 400;
    private readonly StringComparison _pathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public string RootPath { get; }
    public string CurrentPath { get; private set; }

    public RootedFileBrowser(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (!Directory.Exists(RootPath))
            throw new DirectoryNotFoundException("Корневой сетевой каталог не найден или недоступен.");
        if ((File.GetAttributes(RootPath) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Корневой каталог-ссылка не поддерживается.");
        CurrentPath = RootPath;
    }

    public void NavigateTo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        EnsureWithinRoot(full);
        EnsureNoLinks(full);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException("Папка недоступна или не существует.");
        CurrentPath = full;
    }

    public bool GoUp()
    {
        if (string.Equals(CurrentPath, RootPath, _pathComparison)) return false;
        string parent = Directory.GetParent(CurrentPath)?.FullName
            ?? throw new IOException("Родительский каталог отсутствует.");
        NavigateTo(parent);
        return true;
    }

    public SharedFolderSnapshot Scan(int maxEntries = DefaultMaxEntries)
    {
        if (maxEntries is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        EnsureWithinRoot(CurrentPath);
        EnsureNoLinks(CurrentPath);
        var entries = new List<SharedFileEntry>(Math.Min(maxEntries, 400));
        bool more = false;
        // Never recurse: a single refresh must not index the entire corporate share.
        foreach (FileSystemInfo item in new DirectoryInfo(CurrentPath).EnumerateFileSystemInfos())
        {
            try
            {
                FileAttributes attributes = item.Attributes;
                // Do not traverse junctions, symlinks or reparse points.
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (entries.Count == maxEntries) { more = true; break; }
                bool directory = (attributes & FileAttributes.Directory) != 0;
                bool supported = !directory &&
                    DocumentFileClassifier.Classify(item.Name).Kind != DocumentFileKind.Unsupported;
                entries.Add(new SharedFileEntry(item.Name, item.FullName, directory, supported));
            }
            catch (FileNotFoundException) { } // Peer deleted entry during the scan.
            catch (DirectoryNotFoundException) { }
        }
        return new SharedFolderSnapshot(RootPath, CurrentPath,
            entries.OrderByDescending(x => x.IsDirectory)
                .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            more);
    }

    /// <summary>
    /// Revalidates the selected file's path before handing it to an already
    /// existing document parser. Permission revocation may still race this call;
    /// downstream open must handle it. Not intended as a sandbox boundary.
    /// </summary>
    public string ResolveOpenableFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var full = Path.GetFullPath(filePath);
        EnsureWithinRoot(full);
        EnsureNoLinks(full);
        if (!File.Exists(full))
            throw new FileNotFoundException("Файл больше не существует или недоступен.", full);
        if (DocumentFileClassifier.Classify(full).Kind == DocumentFileKind.Unsupported)
            throw new NotSupportedException("Этот формат невозможно открыть в ГЭТ Документ.");
        return full;
    }

    private void EnsureWithinRoot(string path)
    {
        string relative = Path.GetRelativePath(RootPath, path);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, _pathComparison) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, _pathComparison))
            throw new UnauthorizedAccessException("Выход за пределы выбранной папки запрещён.");
    }

    private void EnsureNoLinks(string target)
    {
        if ((File.GetAttributes(RootPath) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Переход по ссылкам недоступен.");
        string relative = Path.GetRelativePath(RootPath, target);
        if (relative == ".") return;
        string current = RootPath;
        var components = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < components.Length; i++)
        {
            current = Path.Combine(current, components[i]);
            var attrs = File.GetAttributes(current);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Символические ссылки и junction не открываются.");
        }
    }
}
