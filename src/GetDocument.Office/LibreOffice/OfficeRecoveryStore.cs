using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// Per-user crash-recovery snapshots. Writes an OOXML copy outside the source directory and
/// publishes a manifest only after the copy and its SHA-256 have been verified.
/// Original documents, SMB locks and WORM revisions are never modified by this store.
/// </summary>
public sealed class OfficeRecoveryStore
{
    private const long MaxCopyBytes = 128L * 1024 * 1024;
    private const long MaxEntryBytes = 128L * 1024 * 1024;
    private const long MaxExpandedBytes = 512L * 1024 * 1024;
    private const int MaxEntryCount = 4096;
    private const int MaxCompressionRatio = 500;
    private const int RetainedCopies = 3;
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly string _root;

    public OfficeRecoveryStore(string? root = null)
    {
        _root = Path.GetFullPath(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GET-Document", "recovery"));
    }

    public async Task<OfficeRecoveryCopy> CaptureAsync(
        string sourcePath, Func<string, Task> exportCopy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exportCopy);
        string source = CanonicalSource(sourcePath);
        if (!File.Exists(source)) throw new FileNotFoundException("Исходный документ не найден.", source);

        string extension = AllowedExtension(source);
        string folder = FolderFor(source);
        // Refuse recovery roots, parents or per-document folders that are
        // symbolic links/junctions. Check before and after directory creation.
        // These checks fail closed; they do not defeat hostile TOCTOU changes.
        RejectLinkedDirectories(folder);
        Directory.CreateDirectory(folder);
        RejectLinkedDirectories(folder);
        string name = Guid.NewGuid().ToString("N");
        string temporaryCopy = Path.Combine(folder, name + ".part" + extension);
        string finalCopy = Path.Combine(folder, name + extension);
        string temporaryManifest = Path.Combine(folder, name + ".json.tmp");
        string finalManifest = Path.Combine(folder, name + ".json");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await exportCopy(temporaryCopy);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureValidPackage(temporaryCopy, extension);
            string sha = await HashFileAsync(temporaryCopy, cancellationToken);
            // Re-evaluate the path after the hash operation: it is possible
            // for a concurrent writer to replace it while it is being read.
            RejectLinkedDirectories(folder);
            if (IsLinkedFile(new FileInfo(temporaryCopy)))
                throw new InvalidDataException("Временная аварийная копия подменена ссылкой.");
            File.Move(temporaryCopy, finalCopy);
            var manifest = new RecoveryManifest(source, DateTimeOffset.UtcNow, sha);
            // Never use WriteAllText/Create: a malicious exporter could have
            // created a sibling .json.tmp symlink and redirected the write to
            // an unrelated document. CreateNew rejects every occupied name.
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            if (manifestBytes.Length > 2048)
                throw new InvalidDataException("Манифест аварийной копии превышает допустимый размер.");
            await using (var output = new FileStream(temporaryManifest, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, bufferSize: 4096, options: FileOptions.Asynchronous))
            {
                await output.WriteAsync(manifestBytes, cancellationToken);
                output.Flush(flushToDisk: true);
            }
            // The manifest is the commit marker. Interrupted exports never become recoverable.
            RejectLinkedDirectories(folder);
            if (IsLinkedFile(new FileInfo(finalCopy)) ||
                IsLinkedFile(new FileInfo(temporaryManifest)))
                throw new InvalidDataException("Аварийная копия или манифест подменены ссылкой.");
            File.Move(temporaryManifest, finalManifest);
            Prune(source);
            return new OfficeRecoveryCopy(finalCopy, manifest.CapturedUtc);
        }
        catch
        {
            TryDelete(temporaryCopy);
            TryDelete(temporaryManifest);
            // If publishing failed, do not leave a visible or orphaned recovery entry.
            if (!File.Exists(finalManifest)) TryDelete(finalCopy);
            throw;
        }
    }

    /// <summary>
    /// Read-only list of possible interrupted recovery artifacts for ONE source.
    /// The mere presence or age of a file does not prove the writer has exited.
    /// Never opens file contents, deletes anything, or authorizes restoration.
    /// The scan is non-recursive and bounded across ALL directory entries.
    /// </summary>
    public OfficeRecoveryIncompleteInventory InspectIncomplete(
        string sourcePath, int maxEntries = 256,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maxEntries is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(maxEntries),
                "Допустимый предел осмотра — от 1 до 1024 записей.");
        string source = CanonicalSource(sourcePath);
        string extension = AllowedExtension(source);
        string folder = FolderFor(source);

        // Do not follow linked/junction roots even when the last component
        // is missing. This remains a path-based inspection, not a sandbox.
        RejectLinkedDirectories(folder);
        if (!Directory.Exists(folder))
            return new OfficeRecoveryIncompleteInventory(
                Array.Empty<OfficeRecoveryIncompleteCandidate>(), false);

        var candidates = new List<OfficeRecoveryIncompleteCandidate>();
        bool truncated = false;
        int visited = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(
                     folder, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++visited > maxEntries)
            {
                truncated = true;
                break;
            }

            string name = Path.GetFileName(entry);
            if (!TryClassifyIncompleteName(name, extension, out var kind))
                continue;

            // FileInfo.LinkTarget can identify a dangling symlink without
            // opening its target. Never read file bytes, hashes or JSON here.
            OfficeRecoveryIncompleteStatus status;
            long? length = null;
            DateTimeOffset? changed = null;
            try
            {
                var metadata = new FileInfo(entry);
                metadata.Refresh();
                if (metadata.LinkTarget is not null ||
                    (metadata.Attributes & (FileAttributes.ReparsePoint |
                                            FileAttributes.Directory |
                                            FileAttributes.Device)) != 0)
                {
                    status = OfficeRecoveryIncompleteStatus.UntrustedLinkOrNonFile;
                }
                else
                {
                    length = metadata.Length;
                    changed = new DateTimeOffset(metadata.LastWriteTimeUtc, TimeSpan.Zero);
                    status = OfficeRecoveryIncompleteStatus.RegularUnverified;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.Security.SecurityException)
            {
                // Concurrent disappearance / inaccessible metadata is NOT a
                // clean bill of health. Expose no external target details.
                status = OfficeRecoveryIncompleteStatus.MetadataUnavailable;
            }

            candidates.Add(new OfficeRecoveryIncompleteCandidate(
                name, kind, status, length, changed));
        }

        return new OfficeRecoveryIncompleteInventory(
            candidates.OrderBy(item => item.FileName, StringComparer.Ordinal).ToArray(),
            truncated);
    }

    private static bool TryClassifyIncompleteName(
        string fileName, string extension, out OfficeRecoveryIncompleteKind kind)
    {
        const int GuidLength = 32;
        kind = default;
        if (fileName.Length <= GuidLength)
            return false;

        string tail = fileName[GuidLength..];
        if (tail.Equals(".part" + extension, StringComparison.OrdinalIgnoreCase))
            kind = OfficeRecoveryIncompleteKind.StagedOfficeCopy;
        else if (tail.Equals(".json.tmp", StringComparison.OrdinalIgnoreCase))
            kind = OfficeRecoveryIncompleteKind.TemporaryManifest;
        else
            return false;

        return Guid.TryParseExact(fileName[..GuidLength], "N", out _);
    }

    public OfficeRecoveryCopy? FindLatest(string sourcePath)
    {
        string source = CanonicalSource(sourcePath);
        string extension = AllowedExtension(source);
        string folder = FolderFor(source);
        RejectLinkedDirectories(folder);
        if (!Directory.Exists(folder)) return null;
        OfficeRecoveryCopy? latest = null;
        foreach (string manifestPath in Directory.EnumerateFiles(folder, "*.json"))
        {
            var candidate = ReadValidCopy(source, extension, manifestPath);
            if (candidate is not null && (latest is null || candidate.CapturedUtc > latest.CapturedUtc))
                latest = candidate;
        }
        return latest;
    }

    /// <summary>Call only after the original file has been saved successfully and without conflict.</summary>
    public void Discard(string sourcePath)
    {
        string source = CanonicalSource(sourcePath);
        string extension = AllowedExtension(source);
        string folder = FolderFor(source);
        RejectLinkedDirectories(folder);
        if (!Directory.Exists(folder)) return;
        foreach (string manifest in Directory.EnumerateFiles(folder, "*.json"))
        {
            string name = Path.GetFileNameWithoutExtension(manifest);
            if (!Guid.TryParseExact(name, "N", out _)) continue;
            // Verify ownership before deletion; do not erase recovery data for a different source.
            RecoveryManifest? metadata = ReadManifest(manifest);
            if (metadata is null || !string.Equals(metadata.SourcePath, source, PathComparison)) continue;
            TryDelete(manifest);
            TryDelete(Path.Combine(folder, name + extension));
        }
    }

    private OfficeRecoveryCopy? ReadValidCopy(string source, string extension, string manifestPath)
    {
        string name = Path.GetFileNameWithoutExtension(manifestPath);
        if (!Guid.TryParseExact(name, "N", out _)) return null;
        var manifest = ReadManifest(manifestPath);
        if (manifest is null || !string.Equals(manifest.SourcePath, source, PathComparison)) return null;
        if (manifest.CapturedUtc == default || manifest.CapturedUtc > DateTimeOffset.UtcNow.AddMinutes(5)) return null;
        string copy = Path.Combine(FolderFor(source), name + extension);
        try
        {
            EnsureValidPackage(copy, extension);
            string sha = HashFile(copy);
            if (!string.Equals(sha, manifest.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
            return new OfficeRecoveryCopy(copy, manifest.CapturedUtc);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                                   NotSupportedException)
        {
            return null;
        }
    }

    private void Prune(string source)
    {
        string extension = AllowedExtension(source);
        string folder = FolderFor(source);
        var entries = Directory.EnumerateFiles(folder, "*.json")
            .Select(manifest => new { Manifest = manifest, Copy = ReadValidCopy(source, extension, manifest) })
            .Where(entry => entry.Copy is not null)
            .OrderByDescending(entry => entry.Copy!.CapturedUtc)
            .Skip(RetainedCopies);
        foreach (var entry in entries)
        {
            TryDelete(entry.Manifest);
            TryDelete(entry.Copy!.FilePath);
        }
    }

    /// <summary>
    /// Do not traverse pre-existing symbolic links or junctions anywhere in
    /// the recovery tree. Missing components are permitted only before mkdir;
    /// filesystem race resistance requires an OS-handle-bound implementation.
    /// </summary>
    private static void RejectLinkedDirectories(string directory)
    {
        for (DirectoryInfo? part = new(directory); part is not null; part = part.Parent)
        {
            part.Refresh();
            // LinkTarget is needed even if Exists is false for a broken link.
            if (part.LinkTarget is not null ||
                (part.Exists && (part.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new InvalidDataException(
                    "Аварийное восстановление через символическую ссылку или junction запрещено.");
        }
    }

    private static bool IsLinkedFile(FileInfo file)
    {
        file.Refresh();
        return file.LinkTarget is not null ||
               (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0);
    }

    private static RecoveryManifest? ReadManifest(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (IsLinkedFile(info) || info.Length > 2048) return null;
            return JsonSerializer.Deserialize<RecoveryManifest>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void EnsureValidPackage(string path, string extension)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || info.Length > MaxCopyBytes)
            throw new InvalidDataException("Резервная копия отсутствует или превышает лимит 128 МиБ.");
        if (IsLinkedFile(info))
            throw new InvalidDataException("Символическая ссылка не является независимой резервной копией.");

        // This is a bounded ZIP envelope check, NOT a complete OOXML semantic check or AV scan.
        // Reuse the same ZIP safety limits as the server's OfficePackageGuard where practical,
        // without pulling a server project into the offline desktop/editor dependency graph.
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count is < 2 or > MaxEntryCount)
            throw new InvalidDataException("Недопустимое количество ZIP-частей в аварийной копии.");

        string mainPart = extension switch
        {
            ".docx" => "word/document.xml",
            ".xlsx" => "xl/workbook.xml",
            _ => "ppt/presentation.xml"
        };
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedTotal = 0;
        foreach (var entry in archive.Entries)
        {
            string name = entry.FullName;
            string[] segments = name.TrimEnd('/').Split('/');
            if (string.IsNullOrEmpty(name) || name.StartsWith("/", StringComparison.Ordinal) ||
                name.Contains('\\') || name.Contains(':') || name.Any(char.IsControl) ||
                segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
                throw new InvalidDataException("Недопустимый путь ZIP-части аварийной копии.");
            if (!parts.Add(name.TrimEnd('/')))
                throw new InvalidDataException("Повторяющееся имя ZIP-части аварийной копии.");

            // Reject Unix symlink entries, as well as macro/ActiveX content in non-macro OOXML.
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                name.EndsWith("/vbaProject.bin", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("activeX/", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("/activeX/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Запрещённая ZIP-часть аварийной копии.");

            if (name.EndsWith("/", StringComparison.Ordinal))
            {
                if (entry.Length != 0)
                    throw new InvalidDataException("ZIP-каталог не должен содержать данные.");
                continue;
            }
            if (entry.Length < 0 || entry.Length > MaxEntryBytes ||
                expandedTotal > MaxExpandedBytes - entry.Length)
                throw new InvalidDataException("Распакованные части аварийной копии слишком велики.");
            expandedTotal += entry.Length;
            if (entry.Length > 4096 &&
                (entry.CompressedLength == 0 ||
                 entry.Length > entry.CompressedLength * (long)MaxCompressionRatio))
                throw new InvalidDataException("Подозрительная степень сжатия аварийной копии.");

            // ZIP metadata alone does not prove recoverability. Stream each part
            // without extracting or buffering it in memory. XML must be parseable
            // and may not carry DTD/external-entity declarations.
            ValidateEntryContents(entry);
        }
        if (!parts.Contains("[Content_Types].xml") || !parts.Contains(mainPart))
            throw new InvalidDataException("В аварийной копии отсутствуют основные OOXML-части.");
    }

    private static void ValidateEntryContents(ZipArchiveEntry entry)
    {
        try
        {
            using Stream content = entry.Open();
            if (entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = XmlReader.Create(content, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaxEntryBytes,
                    IgnoreComments = true
                });
                bool hasRoot = false;
                while (reader.Read())
                    if (reader.NodeType == XmlNodeType.Element)
                        hasRoot = true;
                if (!hasRoot)
                    throw new InvalidDataException("XML-часть аварийной копии не содержит корневого элемента.");
            }
            else
            {
                // In addition to central-directory checks, verify actual stream
                // lengths so a truncated archive cannot masquerade as a valid copy.
                byte[] buffer = new byte[65536];
                long readTotal = 0;
                int read;
                while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
                {
                    readTotal += read;
                    if (readTotal > entry.Length || readTotal > MaxEntryBytes)
                        throw new InvalidDataException("Распакованная часть превышает объявленный размер.");
                }
                if (readTotal != entry.Length)
                    throw new InvalidDataException("Распакованная часть короче объявленного размера.");
            }
        }
        catch (Exception ex) when (ex is XmlException or NotSupportedException)
        {
            throw new InvalidDataException("Аварийная копия содержит повреждённую или недопустимую ZIP/XML-часть.", ex);
        }
    }

    private static string CanonicalSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Не указан файл.", nameof(path));
        return Path.GetFullPath(path);
    }

    private static string AllowedExtension(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".docx" or ".xlsx" or ".pptx"))
            throw new NotSupportedException("Аварийное восстановление допускает только DOCX, XLSX и PPTX.");
        return extension;
    }

    private string FolderFor(string source)
    {
        string normalized = OperatingSystem.IsWindows() ? source.ToUpperInvariant() : source;
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(_root, key);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed record RecoveryManifest(string SourcePath, DateTimeOffset CapturedUtc, string Sha256);
}

public sealed record OfficeRecoveryCopy(string FilePath, DateTimeOffset CapturedUtc);

public enum OfficeRecoveryIncompleteKind
{
    StagedOfficeCopy,
    TemporaryManifest
}

public enum OfficeRecoveryIncompleteStatus
{
    RegularUnverified,
    UntrustedLinkOrNonFile,
    MetadataUnavailable
}

/// <summary>
/// FileName contains ONLY the final name in the per-source folder, not an
/// arbitrary filesystem path. All metadata is unverified and read-only.
/// </summary>
public sealed record OfficeRecoveryIncompleteCandidate(
    string FileName,
    OfficeRecoveryIncompleteKind Kind,
    OfficeRecoveryIncompleteStatus Status,
    long? LengthBytes,
    DateTimeOffset? LastWriteUtc);

/// <summary>
/// Truncated=true means the inspector hit its budget and is NOT an exhaustive
/// list. It never grants deletion, restoration or production SaveAs rights.
/// </summary>
public sealed record OfficeRecoveryIncompleteInventory(
    IReadOnlyList<OfficeRecoveryIncompleteCandidate> Candidates,
    bool Truncated);
