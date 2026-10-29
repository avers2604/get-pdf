using System.Security.Cryptography;
using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// GD-31J.29: an OPT-IN, synthetic-only, single-volume snapshot operation.
/// It never updates source, quarantine, GET/WORM or a user document. A hard
/// link creates a new name atomically without a rename-over-existing race.
/// This is NOT a crash-durability, signature, filesystem sandbox, Office
/// round-trip or production-save certification. See docs for remaining TOCTOU.
/// </summary>
// Only visible to explicitly designated test assemblies via InternalsVisibleTo.
// This cannot be selected by the normal launcher, CLI or production users.
internal enum EuroOfficeSyntheticSnapshotFaultCheckpoint
{
    AfterIntentPersisted,
    AfterPendingFileFlushed,
    AfterPendingJournalFlushed,
    AfterHardLinkCreated,
    AfterLinkedJournalFlushed,
    AfterPendingLinkRemoved,
    AfterFinalHashVerified,
    AfterCompletedJournalFlushed
}

public static class EuroOfficeSyntheticReviewedSnapshot
{
    private const long MaxFileBytes = 128L * 1024 * 1024;
    private const string ManifestFile = "gd31j6-fixture-manifest.json";

    public sealed record Receipt(string FileName, string Sha256, long Length)
    {
        public bool SyntheticOnly => true;
        public bool WormUpdated => false;
        public bool AuthorizedForPublication => false;
        public bool SaveCompleted => false;
        public bool DurableAcrossPowerLoss => false;
    }

    public static string ExpectedName(Guid sessionId, string format)
    {
        if (sessionId == Guid.Empty || format is not ("docx" or "xlsx" or "pptx"))
            throw new ArgumentException("Недопустимый синтетический результат.");
        return "gd31j29-reviewed-" + sessionId.ToString("N") + "." + format;
    }

    /// <summary>
    /// Create an immutable-by-convention LAB copy inside an existing "reviewed"
    /// folder. The source/quarantine are never overwritten, moved or deleted.
    /// No live business service calls this method.
    /// </summary>
    public static Receipt Commit(
        string stagingRoot,
        EuroOfficeNativeSaveAsLab.Review review,
        bool labApproved = false) =>
        CommitCore(stagingRoot, review, labApproved, faultProbe: null);

    /// <summary>
    /// TEST-ONLY controlled exception seam for the eight synthetic write
    /// boundaries. This method never kills a process and does not simulate
    /// OS page cache, power loss or directory durability. It is internal,
    /// requires the same explicit lab approval, and cannot bypass evidence.
    /// </summary>
    internal static Receipt CommitWithFaultProbe(
        string stagingRoot, EuroOfficeNativeSaveAsLab.Review review,
        Action<EuroOfficeSyntheticSnapshotFaultCheckpoint> faultProbe,
        bool labApproved = false)
    {
        if (!labApproved)
            throw new NotSupportedException("Лабораторная фиксация не разрешена.");
        ArgumentNullException.ThrowIfNull(faultProbe);
        return CommitCore(stagingRoot, review, labApproved, faultProbe);
    }

    private static Receipt CommitCore(
        string stagingRoot, EuroOfficeNativeSaveAsLab.Review review,
        bool labApproved,
        Action<EuroOfficeSyntheticSnapshotFaultCheckpoint>? faultProbe)
    {
        if (!labApproved)
            throw new NotSupportedException("Лабораторная публикация не разрешена.");
        ArgumentNullException.ThrowIfNull(review);
        if (!review.CandidateValidatedAsOoxml ||
            review.Candidate is null ||
            review.NativeEvidence is not { } native ||
            native.Core != review.Receipt ||
            native.FailureCode != "candidate_untrusted" ||
            native.Phase != "quarantined" ||
            !native.CorrelatedNativeCallbacks ||
            review.Lifecycle.Status !=
                EuroOfficeNativeSaveAsLifecycle.State.SavedCandidateValidatedButUntrusted)
            throw new InvalidDataException("Нет достоверной корреляции нативной синтетической операции.");

        if (string.IsNullOrWhiteSpace(stagingRoot) ||
            !Path.IsPathFullyQualified(stagingRoot) ||
            stagingRoot.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("Только абсолютный локальный синтетический staging.");
        string root = Path.GetFullPath(stagingRoot);
        if (OperatingSystem.IsWindows() &&
            root.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("UNC/SMB недопустимы.");
        string source = Path.Combine(root, native.Core.FixtureName);
        string quarantine = Path.Combine(root, native.Core.CandidateName);
        string manifest = Path.Combine(root, ManifestFile);
        string reviewed = Path.Combine(root, "reviewed");
        string finalName = ExpectedName(native.Core.SessionId, native.Core.SelectedFormat);
        string destination = Path.Combine(reviewed, finalName);

        if (native.Core.CandidateName !=
                EuroOfficeNativeSaveAsReceipt.CandidateNameFor(
                    native.Core.SessionId, native.Core.SelectedFormat) ||
            EuroOfficeNativeSaveAsReceipt.ValidateFormat(
                native.Core.FixtureName, native.Core.SelectedFormat) !=
                    native.Core.SelectedFormat)
            throw new InvalidDataException("Имя карантинного файла или формат не разрешены.");

        RequireRegularPath(root, expectDirectory: true);
        RequireRegularPath(reviewed, expectDirectory: true);
        RequireRegularPath(manifest, expectDirectory: false);
        RequireRegularPath(source, expectDirectory: false);
        RequireRegularPath(quarantine, expectDirectory: false);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("Синтетическая редакция с таким ID уже зафиксирована.");
        RequireManifest(manifest, native.Core.FixtureName,
            native.Core.SourceBeforeSha256, source);
        // Verify the entire untrusted OOXML again immediately before staging.
        var actual = EuroOfficeNativeArtifactCandidate.InspectRevision(
            root, source, quarantine);
        if (actual.Sha256 != review.Candidate.Sha256 ||
            actual.Length != review.Candidate.Length ||
            actual.Sha256 != native.Core.CandidateSha256 ||
            actual.Length != native.Core.CandidateLength ||
            actual.Sha256 == native.Core.SourceBeforeSha256)
            throw new InvalidDataException("Кандидат изменился после первоначальной проверки.");

        // Unique, private pending name INSIDE the FINAL directory: no copy
        // across filesystems when creating the final directory entry.
        // On crash/failure, leave unverified .tmp for MANUAL inspection,
        // never sweep arbitrary user or pre-existing files.
        // Journal is created BEFORE the first pending byte. An unexpected
        // exception/crash leaves evidence; nothing attempts auto-deletion.
        var attempt = EuroOfficeSyntheticSnapshotJournal.Begin(
            root, native.Core.SessionId, native.Core.SelectedFormat,
            actual.Sha256, actual.Length, Guid.NewGuid());
        faultProbe?.Invoke(EuroOfficeSyntheticSnapshotFaultCheckpoint.AfterIntentPersisted);
        string pending = Path.Combine(reviewed, attempt.PendingName);
        using (var input = new FileStream(
            quarantine, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(
            pending, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 65536, options: FileOptions.WriteThrough))
        {
            if (input.Length != actual.Length || input.Length > MaxFileBytes)
                throw new InvalidDataException("Изменился размер исходного карантинного файла.");
            input.CopyTo(output, bufferSize: 65536);
            output.Flush(flushToDisk: true);
        }
        faultProbe?.Invoke(EuroOfficeSyntheticSnapshotFaultCheckpoint.AfterPendingFileFlushed);
        // Check what was written, instead of trusting copy completion.
        string pendingHash = HashBounded(pending, out long pendingLength);
        if (pendingHash != actual.Sha256 || pendingLength != actual.Length ||
            HashBounded(quarantine, out _) != actual.Sha256 ||
            HashBounded(source, out _) != native.Core.SourceBeforeSha256)
            throw new InvalidDataException("Синтетическая новая редакция изменилась во время записи.");
        EuroOfficeSyntheticSnapshotJournal.Append(attempt, "pending_flushed");
        faultProbe?.Invoke(EuroOfficeSyntheticSnapshotFaultCheckpoint.AfterPendingJournalFlushed);
        // The snapshot may be altered by a concurrent writer *after* the
        // original pending hash check or journal append. Recheck both
        // immediately before the hardlink. This narrows (not closes) TOCTOU;
        // a private VM/account and no shared mounts remain mandatory.
        if (HashBounded(pending, out long preLinkLength) != actual.Sha256 ||
            preLinkLength != actual.Length ||
            HashBounded(quarantine, out _) != actual.Sha256 ||
            HashBounded(source, out _) != native.Core.SourceBeforeSha256)
            throw new InvalidDataException("Подмена файла перед фиксацией синтетической редакции.");
        EuroOfficeSyntheticSnapshotJournal.RequireLastStage(attempt, "pending_flushed");
        RequireRegularPath(reviewed, expectDirectory: true);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("Конфликт: синтетическая новая редакция уже появилась.");

        // .NET 10 does not expose File.CreateHardLink; the explicit native
        // Windows/Linux adapter creates a single no-replace directory entry
        // without fallback. This does NOT fsync directory metadata.
        EuroOfficeNativeHardLinkNoReplace.Create(destination, pending);
        faultProbe?.Invoke(EuroOfficeSyntheticSnapshotFaultCheckpoint.AfterHardLinkCreated);
        EuroOfficeSyntheticSnapshotJournal.Append(attempt, "linked");
        faultProbe?.Invoke(EuroOfficeSyntheticSnapshotFaultCheckpoint.AfterLinkedJournalFlushed);
        // We remove ONLY our uniquely created private pending link, NEVER the
        // source/quarantine/reviewed output. If it fails, throw; operator can
        // inspect both links in an offline VM.
        File.Delete(pending);
        faultProbe?.Invoke(EuroOfficeSyntheticSnapshotFaultCheckpoint.AfterPendingLinkRemoved);
        if (HashBounded(destination, out long committedLength) != actual.Sha256 ||
            committedLength != actual.Length ||
            HashBounded(source, out _) != native.Core.SourceBeforeSha256 ||
            HashBounded(quarantine, out _) != actual.Sha256)
            throw new InvalidDataException("Повторная проверка новой синтетической редакции не пройдена.");

        faultProbe?.Invoke(EuroOfficeSyntheticSnapshotFaultCheckpoint.AfterFinalHashVerified);
        EuroOfficeSyntheticSnapshotJournal.Append(attempt, "completed");
        faultProbe?.Invoke(EuroOfficeSyntheticSnapshotFaultCheckpoint.AfterCompletedJournalFlushed);
        return new Receipt(finalName, actual.Sha256, actual.Length);
    }

    private static void RequireManifest(
        string manifest, string fixture, string expectedSha, string source)
    {
        try
        {
            var info = new FileInfo(manifest);
            if (info.Length is <= 0 or > 16 * 1024)
                throw new InvalidDataException("Отсутствует утверждённый синтетический манифест.");
            using var stream = File.OpenRead(manifest);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 6
            });
            var root = document.RootElement;
            if (root.GetProperty("schema").GetString() != "gd31j6-office-fixtures/1" ||
                root.GetProperty("purpose").GetString() !=
                    "test-only-local-ooxml-no-corporate-documents")
                throw new InvalidDataException("Манифест не разрешает синтетическую лабораторию.");
            var files = root.GetProperty("files");
            if (files.ValueKind != JsonValueKind.Object ||
                files.EnumerateObject().Count() != 3)
                throw new InvalidDataException("Некорректный набор тестовых fixtures.");
            var entry = files.GetProperty(fixture);
            var length = new FileInfo(source).Length;
            if (entry.GetProperty("size").GetInt64() != length ||
                entry.GetProperty("sha256").GetString() != expectedSha ||
                HashBounded(source, out _) != expectedSha)
                throw new InvalidDataException("Исходный fixture не совпадает с манифестом.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException
                                   or InvalidOperationException)
        {
            throw new InvalidDataException("Неполный или некорректный манифест.", ex);
        }
    }

    private static string HashBounded(string path, out long size)
    {
        RequireRegularPath(path, expectDirectory: false);
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read);
        size = stream.Length;
        if (size is <= 0 or > MaxFileBytes)
            throw new InvalidDataException("Превышен допустимый размер локального синтетического файла.");
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static void RequireRegularPath(string path, bool expectDirectory)
    {
        // No symlink or Windows reparse-point, including any existing
        // ancestor; requires a private lab account with exclusive staging.
        string full = Path.GetFullPath(path);
        string? parent = full;
        while (parent is not null)
        {
            if (!File.Exists(parent) && !Directory.Exists(parent))
                throw new InvalidDataException("Локальный синтетический путь отсутствует.");
            FileAttributes attributes = File.GetAttributes(parent);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Ссылки/reparse-point запрещены.");
            string? next = Path.GetDirectoryName(parent);
            if (next == parent) break;
            parent = next;
        }
        bool actualDirectory = Directory.Exists(full);
        if (actualDirectory != expectDirectory)
            throw new InvalidDataException("Несоответствие типа каталога/файла.");
    }
}
