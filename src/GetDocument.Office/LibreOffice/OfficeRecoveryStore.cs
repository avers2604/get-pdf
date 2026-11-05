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
    // Bound manifest path materialization independently of ZIP entry count.
    // Over-budget lookup fails with an error, never with "no copies".
    private const int MaxManifestCandidates = 4096;
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly string _root;
    // Internal deterministic test seam only; no volume override in public API.
    private readonly Func<string, RecoveryCaptureVolumeObservation>? _testVolumeObservation;

    public OfficeRecoveryStore(string? root = null)
    {
        _root = Path.GetFullPath(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GET-Document", "recovery"));
    }

    internal OfficeRecoveryStore(
        string root, Func<string, RecoveryCaptureVolumeObservation> testVolumeObservation)
        : this(root)
    {
        _testVolumeObservation = testVolumeObservation
            ?? throw new ArgumentNullException(nameof(testVolumeObservation));
    }

    public async Task<OfficeRecoveryCopy> CaptureAsync(
        string sourcePath, Func<string, Task> exportCopy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exportCopy);
        string source = CanonicalSource(sourcePath);
        if (!File.Exists(source)) throw new FileNotFoundException("Исходный документ не найден.", source);

        string extension = AllowedExtension(source);
        string folder = FolderFor(source);

        // GD-31J.27bm: ONE cooperative lock for all sources using this
        // recovery root. Hold it across admission, exporter awaits, manifest
        // commit and pruning; disposal releases the OS-held byte-range lock
        // even if the exporter fails or cancellation is requested.
        // Network/unknown volumes must fail closed. This does not establish
        // cross-host SMB locking or protect against uncooperative processes.
        using var captureLease = await RecoveryCaptureVolumeGate.AcquireAsync(
            _root, _testVolumeObservation, cancellationToken);

        // Do not start another 128 MiB export when this source already has
        // too many untrusted incomplete artifacts (including final copies
        // with no observed manifest). Admission never deletes any artifact.
        // This is a conservative per-source limit, NOT a cross-process lock
        // or a complete disk-space budget.
        RecoveryCaptureAdmission.EnsureMayCapture(
            InspectIncomplete(source, maxEntries: 1024, cancellationToken: cancellationToken));

        // Refuse recovery roots, parents or per-document folders that are
        // symbolic links/junctions. Check before and after directory creation.
        // These checks fail closed; they do not defeat hostile TOCTOU changes.
        RejectLinkedDirectories(folder);
        Directory.CreateDirectory(folder);
        RejectLinkedDirectories(folder);
        // GD-31J.27bv: path-based Prune is suspended; admit at most 32
        // committed recovery manifests per source. Do not begin exporter
        // work when only unverified destructive cleanup could make space.
        RecoveryPairMutationGate.RequireCaptureBudget(
            Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly));
        string name = Guid.NewGuid().ToString("N");
        string temporaryCopy = Path.Combine(folder, name + ".part" + extension);
        string finalCopy = Path.Combine(folder, name + extension);
        string temporaryManifest = Path.Combine(folder, name + ".json.tmp");
        string finalManifest = Path.Combine(folder, name + ".json");
        // The path-only exporter cannot return an OS-held identity/ownership
        // receipt for the file it creates. On failure, do NOT infer ownership
        // from an unpredictable filename or the fact that a rename succeeded.
        // Uncommitted artifacts are left for read-only inventory and a future
        // native identity-bound cleanup protocol.
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await exportCopy(temporaryCopy);
            cancellationToken.ThrowIfCancellationRequested();
            // Hash and ZIP must observe the same opened filesystem object.
            string sha = await VerifyAndHashExportAsync(
                temporaryCopy, extension, cancellationToken);
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
            // Final cooperative cancellation checkpoint before the manifest
            // becomes the commit marker. After File.Move succeeds the
            // snapshot is published and must not be rolled back on cancel.
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryManifest, finalManifest);
            Prune(source);
            return new OfficeRecoveryCopy(finalCopy, manifest.CapturedUtc);
        }
        catch
        {
            // Fail closed. A path is not an ownership token: the exporter,
            // another process or an attacker may have replaced any name.
            // This applies equally to .part, .json.tmp and an uncommitted
            // renamed copy. Never delete by pathname during rollback.
            //
            // FindLatest requires a committed, verified manifest and will
            // never offer an incomplete copy. Read-only inventory can report
            // leftovers; actual cleanup needs an identity-bound handle.
            // If the manifest was committed before Prune failed, keep it.
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
        => InspectIncompleteWithEnumerator(sourcePath, maxEntries, cancellationToken,
            folder => Directory.EnumerateFileSystemEntries(
                folder, "*", SearchOption.TopDirectoryOnly));

    // Internal seam for deterministic faults AFTER a successful directory
    // probe; never treats a failed enumeration as a clean, empty inventory.
    internal OfficeRecoveryIncompleteInventory InspectIncompleteWithEnumerator(
        string sourcePath, int maxEntries, CancellationToken cancellationToken,
        Func<string, IEnumerable<string>> enumerateEntries)
    {
        ArgumentNullException.ThrowIfNull(enumerateEntries);
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
        if (!RecoveryFolderExists(folder))
            return new OfficeRecoveryIncompleteInventory(
                Array.Empty<OfficeRecoveryIncompleteCandidate>(), false);

        // Make a single bounded *name* observation before checking metadata.
        // In particular, a .docx/.xlsx/.pptx final copy must NOT be described
        // as manifest-free until the entire bounded directory was observed.
        // The result remains a race-prone observation, never delete authority.
        var observedEntries = new List<string>(maxEntries);
        bool truncated = false;
        int visited = 0;
        string expectedParent = Path.GetFullPath(folder);
        foreach (string entry in enumerateEntries(folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++visited > maxEntries)
            {
                truncated = true;
                break;
            }

            // Deterministic test enumerators must stay scoped to the source.
            // Paths are only inspected; no entry is opened by pathname.
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(entry)),
                    expectedParent, PathComparison))
                throw new InvalidDataException(
                    "Элемент осмотра находится вне каталога аварийного восстановления.");
            observedEntries.Add(entry);
        }

        var observedNames = new HashSet<string>(
            observedEntries.Select(entry => Path.GetFileName(entry)!),
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var candidates = new List<OfficeRecoveryIncompleteCandidate>();
        foreach (string entry in observedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(entry);
            if (!TryClassifyIncompleteName(name, extension, out var kind))
                continue;

            // No inference of absence from a truncated enumeration. A name
            // with ANY matching .json entry is not classified as orphan,
            // even if the manifest is unreadable or malformed. The manifest
            // may also appear after this scan; that race must remain benign.
            if (kind == OfficeRecoveryIncompleteKind.FinalCopyWithoutObservedManifest &&
                (truncated || observedNames.Contains(name[..32] + ".json")))
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
            truncated, ObservedEntries: observedEntries.Count);
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
        else if (tail.Equals(extension, StringComparison.OrdinalIgnoreCase))
            kind = OfficeRecoveryIncompleteKind.FinalCopyWithoutObservedManifest;
        else
            return false;

        return Guid.TryParseExact(fileName[..GuidLength], "N", out _);
    }

    public OfficeRecoveryCopy? FindLatest(string sourcePath)
        => FindLatestWithEnumerator(sourcePath,
            folder => Directory.EnumerateFiles(folder, "*.json"));

    // An enumeration that fails after the probe (including lazy MoveNext)
    // must propagate its OS error instead of returning a partial/empty scan.
    internal OfficeRecoveryCopy? FindLatestWithEnumerator(
        string sourcePath, Func<string, IEnumerable<string>> enumerateManifests,
        Func<string, OfficeRecoveryCopy?>? verifyCandidate = null)
    {
        ArgumentNullException.ThrowIfNull(enumerateManifests);
        string source = CanonicalSource(sourcePath);
        string extension = AllowedExtension(source);
        string folder = FolderFor(source);
        RejectLinkedDirectories(folder);
        if (!RecoveryFolderExists(folder)) return null;

        // Complete bounded enumeration FIRST. If lazy MoveNext fails at any
        // point, do not publish an incomplete or falsely empty selection.
        string[] manifestPaths = enumerateManifests(folder)
            .Take(MaxManifestCandidates + 1).ToArray();
        if (manifestPaths.Length > MaxManifestCandidates)
            throw new InvalidDataException(
                "Слишком много манифестов аварийных копий для безопасной проверки.");

        // Cheap JSON metadata is only a sorting HINT. A candidate still gets
        // a fresh full manifest, SHA-256 and ZIP/XML check before returning.
        // Invalid or foreign manifests never enter the expensive queue.
        DateTimeOffset latestAllowed = DateTimeOffset.UtcNow.AddMinutes(5);
        var observations = manifestPaths
            .Where(path => Guid.TryParseExact(
                Path.GetFileNameWithoutExtension(path), "N", out _))
            .Select(path => new { Path = path, Metadata = ReadManifest(path) })
            .ToArray();
        var ordered = observations
            .Where(entry => entry.Metadata is not null &&
                string.Equals(entry.Metadata.SourcePath, source, PathComparison) &&
                entry.Metadata.CapturedUtc != default &&
                entry.Metadata.CapturedUtc <= latestAllowed)
            .OrderByDescending(entry => entry.Metadata!.CapturedUtc);

        // A preliminary JSON timestamp is an untrusted ordering hint.
        // Concurrent edits to any observed manifest, or additions/removals
        // from the manifest set, invalidate the entire selection. Never return
        // a partially checked newest candidate on a moving dataset.
        void EnsureSurveyStillValid()
        {
            string[] currentPaths = enumerateManifests(folder)
                .Take(MaxManifestCandidates + 1).ToArray();
            var comparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (currentPaths.Length > MaxManifestCandidates ||
                currentPaths.Length != manifestPaths.Length ||
                !new HashSet<string>(manifestPaths, comparer).SetEquals(currentPaths))
                throw new InvalidDataException(
                    "Набор манифестов изменился во время поиска аварийных копий.");
            foreach (var observation in observations)
            {
                if (!Equals(observation.Metadata, ReadManifest(observation.Path)))
                    throw new InvalidDataException(
                        "Манифест изменился во время поиска аварийных копий.");
            }
        }

        foreach (var entry in ordered)
        {
            // The optional delegate is internal-only deterministic test
            // instrumentation; real callers ALWAYS use ReadValidCopy.
            OfficeRecoveryCopy? candidate = verifyCandidate is null
                ? ReadValidCopy(source, extension, entry.Path)
                : verifyCandidate(entry.Path);
            if (candidate is not null)
            {
                // A manifest can change between preliminary sort and the
                // final candidate check. Reject stale ranking, not just hashes.
                if (candidate.CapturedUtc != entry.Metadata!.CapturedUtc)
                    throw new InvalidDataException(
                        "Время манифеста изменилось во время проверки копии.");
                EnsureSurveyStillValid();
                return candidate;
            }
        }
        EnsureSurveyStillValid();
        return null;
    }

    /// <summary>
    /// GD-31J.27bv: post-save notification. Name-based deletion is
    /// intentionally suspended until an independently verified OS-native
    /// identity-bound deletion backend exists. Preserves all copies.
    /// </summary>
    public void Discard(string sourcePath)
    {
        string source = CanonicalSource(sourcePath);
        _ = AllowedExtension(source);
        string folder = FolderFor(source);
        RejectLinkedDirectories(folder);
        // A path, hash and FileInfo preflight are NOT a deletion capability.
        // The caller's successful Save must not fail solely because cleanup
        // cannot be proved safe. Expose preserved artifacts via diagnostics.
        // No deletion callback is invoked.
    }

    // Internal-only test seam to simulate a file edit precisely after SHA
    // calculation. Normal production search never supplies this callback.
    internal OfficeRecoveryCopy? ReadValidCopyWithPostHashMutation(
        string sourcePath, string manifestPath, Action<string> afterHash)
    {
        ArgumentNullException.ThrowIfNull(afterHash);
        string source = CanonicalSource(sourcePath);
        return ReadValidCopy(source, AllowedExtension(source), manifestPath, afterHash);
    }

    private OfficeRecoveryCopy? ReadValidCopy(
        string source, string extension, string manifestPath, Action<string>? afterHash = null)
    {
        string name = Path.GetFileNameWithoutExtension(manifestPath);
        if (!Guid.TryParseExact(name, "N", out _)) return null;
        var manifest = ReadManifest(manifestPath);
        if (manifest is null || !string.Equals(manifest.SourcePath, source, PathComparison)) return null;
        if (manifest.CapturedUtc == default || manifest.CapturedUtc > DateTimeOffset.UtcNow.AddMinutes(5)) return null;
        string copy = Path.Combine(FolderFor(source), name + extension);
        try
        {
            // Before hashing, refuse linked, missing and oversized paths.
            // This preflight is required: hashing before the ZIP guard without
            // it could follow an unsafe symlink or read arbitrary huge bytes.
            var info = new FileInfo(copy);
            if (!info.Exists || info.Length == 0 || info.Length > MaxCopyBytes ||
                IsLinkedFile(info))
                return null;
            long beforeLength = info.Length;
            DateTime beforeModifiedUtc = info.LastWriteTimeUtc;
            // Hold exactly one read handle for SHA and the ZIP/XML scan.
            // FileShare.Read also rejects cooperative writers on Windows;
            // POSIX and SMB mutation semantics need real platform testing.
            using var input = OpenRecoveryReadStream(copy);
            if (input.Length != beforeLength) return null;
            if (!VerifyOpenedPackage(input, extension, manifest.Sha256,
                    afterHash is null ? null : () => afterHash(copy)))
                return null;
            var after = new FileInfo(copy);
            after.Refresh();
            // This detects ordinary concurrent replacement or modification.
            // Equal-size/equal-timestamp hostile swaps still require an OS
            // handle-bound verification protocol (not provided here).
            if (!after.Exists || IsLinkedFile(after) ||
                after.Length != beforeLength ||
                after.LastWriteTimeUtc != beforeModifiedUtc)
                return null;
            return new OfficeRecoveryCopy(copy, manifest.CapturedUtc);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                                   NotSupportedException or System.Security.SecurityException)
        {
            // A single unreadable/replaced copy is NOT proof that the other
            // manifests in this per-document folder are invalid.
            return null;
        }
    }

    private void Prune(string source)
    {
        // Retention target is suspended while native identity-bound unlink
        // has not passed Windows/Astra/SMB E2E. Do NOT remove by mutable name.
        // Capture admission still applies pressure limits to incomplete items.
        _ = source;
        // No deletion callback is invoked.
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
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or
                                   System.Security.SecurityException)
        {
            // Malformed, oversized or access-denied individual JSON entries
            // are never trusted and do not hide other valid snapshots.
            return null;
        }
    }

    private static void EnsureValidPackage(Stream input, string extension)
    {
        if (!input.CanRead || !input.CanSeek ||
            input.Length == 0 || input.Length > MaxCopyBytes)
            throw new InvalidDataException(
                "Резервная копия не поддерживает безопасное чтение или превышает 128 МиБ.");

        // Read only the SAME caller-owned seekable handle as SHA-256.
        // ZipArchive must not close this stream: the caller checks its
        // metadata and disposes the handle after both validations.
        input.Position = 0;
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        // This is a bounded ZIP envelope check, NOT a complete OOXML semantic check or AV scan.
        // Reuse the same ZIP safety limits as the server's OfficePackageGuard where practical,
        // without pulling a server project into the offline desktop/editor dependency graph.
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

    /// <summary>
    /// Only a confirmed missing directory means "no recovery files".
    /// Directory.Exists returns false for some ACL/IO failures as well as
    /// absence, so it is NOT suitable for recovery status decisions.
    /// Other failures deliberately propagate to the caller (and the UI).
    /// A network host reporting "path not found" is indistinguishable from
    /// genuine absence at this OS API level; see the manual SMB gate.
    /// </summary>
    internal static bool RecoveryFolderExists(
        string folder, Func<string, FileAttributes>? getAttributes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        try
        {
            FileAttributes attributes = getAttributes is null
                ? File.GetAttributes(folder)
                : getAttributes(folder);
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    "Путь аварийного восстановления не является обычным каталогом.");
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private string FolderFor(string source)
    {
        string normalized = OperatingSystem.IsWindows() ? source.ToUpperInvariant() : source;
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(_root, key);
    }

    // Test-only seam that simulates a path replacement after preflight.
    // Normal production call sites never provide this callback.
    internal static FileStream OpenRecoveryReadStreamForTest(
        string path, Action<string> beforeOpen)
    {
        ArgumentNullException.ThrowIfNull(beforeOpen);
        return OpenRecoveryReadStream(path, FileOptions.None, beforeOpen);
    }

    // Use one opened FileStream for SHA and ZIP. Recheck the path immediately
    // AFTER opening and BEFORE any bytes are consumed, so ordinary path
    // replacement and injected symlink swaps fail closed. These are still
    // path-based checks: neither an atomic no-follow open nor an OS file-ID
    // comparison. A privileged/same-metadata swap can evade them.
    private static FileStream OpenRecoveryReadStream(
        string path, FileOptions options = FileOptions.None,
        Action<string>? beforeOpen = null)
    {
        var file = new FileInfo(path);
        file.Refresh();
        if (!file.Exists || file.Length == 0 || file.Length > MaxCopyBytes ||
            IsLinkedFile(file))
            throw new InvalidDataException(
                "Аварийная копия отсутствует, подменена ссылкой или превышает 128 МиБ.");
        long preOpenLength = file.Length;
        DateTime preOpenModifiedUtc = file.LastWriteTimeUtc;

        beforeOpen?.Invoke(path);
        FileStream? input = null;
        try
        {
            input = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 65536, options: options);

            // The opened handle may refer to a different file if the path
            // changed after the first FileInfo check. Verify the name and
            // parent tree again before performing any SHA/ZIP reads.
            RejectLinkedDirectories(Path.GetDirectoryName(path)!);
            var current = new FileInfo(path);
            current.Refresh();
            if (!current.Exists || IsLinkedFile(current) ||
                (current.Attributes & (FileAttributes.Directory |
                                       FileAttributes.Device |
                                       FileAttributes.ReparsePoint)) != 0 ||
                current.Length != preOpenLength ||
                current.LastWriteTimeUtc != preOpenModifiedUtc ||
                !input.CanRead || !input.CanSeek ||
                input.Length != preOpenLength ||
                input.Length > MaxCopyBytes)
                throw new InvalidDataException(
                    "Файл аварийной копии изменился при открытии или не является обычным файлом.");

            return input;
        }
        catch
        {
            // FileStream may already be open, including on an unrelated
            // symlink target. Dispose it on EVERY failed post-open check.
            input?.Dispose();
            throw;
        }
    }

    // Internal stream-level seam: both validators receive the SAME handle.
    // Synthetic tests can simulate a change between SHA and ZIP without
    // touching any actual user files.
    internal static bool VerifyOpenedPackage(
        Stream input, string extension, string expectedSha, Action? afterHash = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead || !input.CanSeek ||
            input.Length == 0 || input.Length > MaxCopyBytes)
            throw new InvalidDataException("ZIP/SHA-проверка требует обычного seekable-файла.");
        input.Position = 0;
        string sha = Convert.ToHexString(SHA256.HashData(input));
        if (!string.Equals(sha, expectedSha, StringComparison.OrdinalIgnoreCase))
            return false;
        afterHash?.Invoke();
        input.Position = 0;
        EnsureValidPackage(input, extension);
        return true;
    }

    // CaptureAsync also validates its staging copy on one handle, retaining
    // asynchronous SHA-256 cancellation and full bounded ZIP/XML inspection.
    private static async Task<string> VerifyAndHashExportAsync(
        string path, string extension, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists || info.Length == 0 || info.Length > MaxCopyBytes ||
            IsLinkedFile(info))
            throw new InvalidDataException("Неверная временная аварийная копия.");
        long beforeLength = info.Length;
        DateTime beforeModifiedUtc = info.LastWriteTimeUtc;
        await using var input = OpenRecoveryReadStream(path, FileOptions.Asynchronous);
        if (input.Length != beforeLength)
            throw new InvalidDataException("Временная копия изменена до проверки.");
        cancellationToken.ThrowIfCancellationRequested();
        EnsureValidPackage(input, extension);
        cancellationToken.ThrowIfCancellationRequested();
        input.Position = 0;
        string sha = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
        var after = new FileInfo(path);
        after.Refresh();
        if (!after.Exists || IsLinkedFile(after) ||
            after.Length != beforeLength ||
            after.LastWriteTimeUtc != beforeModifiedUtc)
            throw new InvalidDataException("Временная копия изменена во время проверки.");
        return sha;
    }

    /// <summary>
    /// Deterministic ordering rule shared by Discard and Prune. The supplied
    /// delete callback must only report success after removing an owned part.
    /// An exception is allowed to propagate from a test callback; production
    /// uses the exception-safe TryDeleteVerifiedPart implementation.
    /// </summary>
    internal static bool DeleteVerifiedPair(
        string copyPath, string manifestPath, Func<string, bool> tryDelete)
    {
        ArgumentNullException.ThrowIfNull(tryDelete);
        if (!tryDelete(copyPath)) return false;
        return tryDelete(manifestPath);
    }

    // GD-31J.27bv: legacy internal cleanup test seam. Without an explicit
    // synthetic delegate, a pathname is NEVER sufficient to authorize
    // deletion. In production this method preserves unowned artifacts.
    // Test callbacks can simulate faults; they must not be wired to a real
    // destructive operation without a separate native identity-bound design.
    internal static void TryDelete(string path, Action<string>? deleteFile = null)
    {
        if (deleteFile is null) return;
        try
        {
            deleteFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Security.SecurityException)
        {
            // Do not hide the original exporter/cancellation exception.
            // Unexpected programming errors are intentionally not swallowed.
        }
    }

    private sealed record RecoveryManifest(string SourcePath, DateTimeOffset CapturedUtc, string Sha256);
}

public sealed record OfficeRecoveryCopy(string FilePath, DateTimeOffset CapturedUtc);

public enum OfficeRecoveryIncompleteKind
{
    StagedOfficeCopy,
    TemporaryManifest,
    // Informational only: the final-name copy had no manifest in the
    // complete bounded name observation. It may belong to a LIVE writer.
    FinalCopyWithoutObservedManifest
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
    bool Truncated,
    // Counts ALL immediate entries observed, including unrecognized names.
    // Not a global disk-space quota and never a delete authorization.
    int ObservedEntries = 0);
