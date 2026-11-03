namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27bk: name-only, read-only observations of temporary private
/// workspaces. A listed directory is NOT proven abandoned or owned and must
/// never be used as an input to automatic deletion or document restoration.
/// This component is deliberately not wired into production recovery UI.
/// </summary>
internal enum RecoveryDetachedWorkspaceKind
{
    PrivatePublication,
    OwnedConsumer,
}

internal enum RecoveryDetachedWorkspaceStatus
{
    DirectoryUnverified,
    UntrustedLinkOrNonDirectory,
    MetadataUnavailable,
}

internal sealed record RecoveryDetachedWorkspaceCandidate(
    string DirectoryName,
    RecoveryDetachedWorkspaceKind Kind,
    RecoveryDetachedWorkspaceStatus Status,
    DateTimeOffset? LastWriteUtc);

internal sealed record RecoveryDetachedWorkspaceInventory(
    IReadOnlyList<RecoveryDetachedWorkspaceCandidate> Candidates,
    bool Truncated);

internal static class RecoveryDetachedWorkspaceInspector
{
    /// <summary>
    /// Inspect ONLY the immediate entries of the temp root. Never open
    /// their contents, follow links, recurse, create or remove anything.
    /// No process/boot timestamp, path prefix or age grants delete authority.
    /// </summary>
    internal static RecoveryDetachedWorkspaceInventory Inspect(
        string? tempRoot = null,
        int maxEntries = 256,
        CancellationToken cancellationToken = default) =>
        InspectWithEnumerator(tempRoot, maxEntries, cancellationToken,
            root => Directory.EnumerateFileSystemEntries(
                root, "*", SearchOption.TopDirectoryOnly));

    // An isolated fault-injection seam: production always enumerates the OS
    // entries above, never paths controlled by another service.
    internal static RecoveryDetachedWorkspaceInventory InspectWithEnumerator(
        string? tempRoot, int maxEntries, CancellationToken cancellationToken,
        Func<string, IEnumerable<string>> enumerateEntries)
    {
        ArgumentNullException.ThrowIfNull(enumerateEntries);
        cancellationToken.ThrowIfCancellationRequested();
        if (maxEntries is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        string root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(tempRoot ?? Path.GetTempPath()));

        // A missing optional scratch root is a true empty inventory, but
        // permissions, link/reparse and malformed-path failures propagate.
        var rootMetadata = new DirectoryInfo(root);
        rootMetadata.Refresh();
        if (rootMetadata.LinkTarget is not null)
            throw new InvalidDataException(
                "Корень временного каталога является ссылкой.");
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(root);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new RecoveryDetachedWorkspaceInventory(
                Array.Empty<RecoveryDetachedWorkspaceCandidate>(), false);
        }

        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                "Корень временного каталога не является обычным каталогом.");
        RejectLinkedDirectoryTree(root);

        var candidates = new List<RecoveryDetachedWorkspaceCandidate>();
        int visited = 0;
        bool truncated = false;
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (string entry in enumerateEntries(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++visited > maxEntries)
            {
                truncated = true;
                break;
            }

            // A test enumerator must not be able to supply a different root.
            string candidatePath = Path.GetFullPath(entry);
            if (!string.Equals(Path.GetDirectoryName(candidatePath), root,
                    pathComparison))
                throw new InvalidDataException(
                    "Элемент временного каталога находится за его пределами.");

            string name = Path.GetFileName(candidatePath);
            if (!TryClassify(name, out RecoveryDetachedWorkspaceKind kind))
                continue;

            RecoveryDetachedWorkspaceStatus status;
            DateTimeOffset? changedUtc = null;
            try
            {
                var metadata = new DirectoryInfo(candidatePath);
                metadata.Refresh();
                if (metadata.LinkTarget is not null ||
                    (metadata.Attributes & (FileAttributes.ReparsePoint |
                                            FileAttributes.Directory)) !=
                    FileAttributes.Directory)
                {
                    status = RecoveryDetachedWorkspaceStatus.UntrustedLinkOrNonDirectory;
                }
                else
                {
                    status = RecoveryDetachedWorkspaceStatus.DirectoryUnverified;
                    changedUtc = new DateTimeOffset(
                        metadata.LastWriteTimeUtc, TimeSpan.Zero);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
            {
                status = RecoveryDetachedWorkspaceStatus.MetadataUnavailable;
            }

            // Return only the name, never the full path or a delete token.
            candidates.Add(new RecoveryDetachedWorkspaceCandidate(
                name, kind, status, changedUtc));
        }

        return new RecoveryDetachedWorkspaceInventory(
            candidates.OrderBy(x => x.DirectoryName, StringComparer.Ordinal).ToArray(),
            truncated);
    }

    private static bool TryClassify(
        string name, out RecoveryDetachedWorkspaceKind kind)
    {
        const string publisherPrefix = "getdoc-recovery-private-";
        const string consumerPrefix = "getdoc-consumer-owned-";
        kind = default;
        string suffix;
        if (name.StartsWith(publisherPrefix, StringComparison.Ordinal))
        {
            kind = RecoveryDetachedWorkspaceKind.PrivatePublication;
            suffix = name[publisherPrefix.Length..];
        }
        else if (name.StartsWith(consumerPrefix, StringComparison.Ordinal))
        {
            kind = RecoveryDetachedWorkspaceKind.OwnedConsumer;
            suffix = name[consumerPrefix.Length..];
        }
        else
        {
            return false;
        }

        // Random names vary between .NET and OS releases. This grammar
        // excludes empty/odd filenames; it never attests provenance.
        return suffix.Length is >= 6 and <= 80 &&
            suffix.Any(char.IsLetterOrDigit) &&
            suffix.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
                or >= '0' and <= '9' or '.' or '_' or '-');
    }

    private static void RejectLinkedDirectoryTree(string root)
    {
        for (DirectoryInfo? part = new(root); part is not null; part = part.Parent)
        {
            part.Refresh();
            if (!part.Exists || part.LinkTarget is not null ||
                (part.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    "Промежуточный каталог содержит ссылку или недоступен.");
        }
    }
}
