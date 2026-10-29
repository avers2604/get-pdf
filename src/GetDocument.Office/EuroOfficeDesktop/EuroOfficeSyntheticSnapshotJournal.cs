using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// GD-31J.30: lab-only stage journal and READ-ONLY pending inspection.
/// Not a trusted audit ledger, filesystem durability proof or deletion policy.
/// Even a name matching a generated name NEVER authorizes cleanup.
/// </summary>
public static class EuroOfficeSyntheticSnapshotJournal
{
    private const string Schema = "getdoc-synthetic-reviewed-journal/1";
    private const string JournalPrefix = ".gd31j30-intent-";
    private const string PendingPrefix = ".gd31j29-pending-";
    private const int MaxJournalBytes = 8192;
    private const int MaxItems = 256;
    private const long MaxFileBytes = 128L * 1024 * 1024;

    public enum Observation
    {
        PendingNeedsReview,
        PendingAfterLink,
        CompleteObserved,
        UncertainAfterLink,
        NoOutputObserved,
        MissingFinal,
        MalformedJournal,
        UnknownPending
    }

    public sealed record Report(
        Guid? AttemptId, string PendingName, string FinalName,
        string LastStage, Observation State)
    {
        public bool TrustedForDeletion => false;
        public bool AuthorizedForPublication => false;
    }

    internal sealed record Attempt(
        Guid AttemptId, Guid SessionId, string Format, string Sha256,
        long Length, string JournalPath, string PendingName, string FinalName);

    private sealed record Entry(
        Guid AttemptId, Guid SessionId, string Format, string Sha256,
        long Length, string PendingName, string FinalName, int Sequence, string Stage);

    /// <summary>
    /// Stage 1 is written and flushed BEFORE the pending file is created.
    /// No source/quarantine/final files are changed here.
    /// </summary>
    internal static Attempt Begin(
        string stagingRoot, Guid sessionId, string format,
        string sha256, long length, Guid attemptId)
    {
        ValidateRoot(stagingRoot, out string reviewed);
        if (sessionId == Guid.Empty || attemptId == Guid.Empty ||
            !ValidSha(sha256) || length is <= 0 or > MaxFileBytes)
            throw new InvalidDataException("Недопустимый ID/хеш/размер журнала.");
        string finalName = EuroOfficeSyntheticReviewedSnapshot.ExpectedName(sessionId, format);
        string pendingName = PendingPrefix + attemptId.ToString("N") + ".tmp";
        string journalPath = Path.Combine(
            reviewed, JournalPrefix + attemptId.ToString("N") + ".jsonl");
        var attempt = new Attempt(attemptId, sessionId, format, sha256,
            length, journalPath, pendingName, finalName);
        byte[] line = Serialize(attempt, sequence: 1, stage: "intent");
        using (var file = new FileStream(journalPath,
            FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, options: FileOptions.WriteThrough))
        {
            file.Write(line);
            file.Flush(flushToDisk: true);
        }
        return attempt;
    }

    /// <summary>
    /// Advance only intent -> pending_flushed -> linked -> completed.
    /// A failed append leaves evidence for manual examination, never cleanup.
    /// </summary>
    internal static void Append(Attempt attempt, string stage)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ValidateRoot(Path.GetDirectoryName(Path.GetDirectoryName(attempt.JournalPath)!)!,
            out string reviewed);
        string expectedPath = Path.Combine(reviewed,
            JournalPrefix + attempt.AttemptId.ToString("N") + ".jsonl");
        if (!string.Equals(Path.GetFullPath(attempt.JournalPath), expectedPath,
                StringComparison.Ordinal) ||
            !string.Equals(attempt.PendingName,
                PendingPrefix + attempt.AttemptId.ToString("N") + ".tmp",
                StringComparison.Ordinal) ||
            !string.Equals(attempt.FinalName,
                EuroOfficeSyntheticReviewedSnapshot.ExpectedName(
                    attempt.SessionId, attempt.Format), StringComparison.Ordinal))
            throw new InvalidDataException("Чужая попытка записи журнала.");
        var entries = ReadJournal(attempt.JournalPath);
        Entry latest = entries[^1];
        if (latest.AttemptId != attempt.AttemptId ||
            latest.SessionId != attempt.SessionId ||
            latest.Format != attempt.Format ||
            latest.Sha256 != attempt.Sha256 ||
            latest.Length != attempt.Length ||
            latest.FinalName != attempt.FinalName ||
            latest.PendingName != attempt.PendingName)
            throw new InvalidDataException("Подменён журнал лабораторной операции.");
        int next = latest.Sequence + 1;
        if (next > 4 ||
            NextStage(latest.Stage) != stage)
            throw new InvalidDataException("Неверный порядок стадий журнала.");
        byte[] bytes = Serialize(attempt, next, stage);
        if (new FileInfo(attempt.JournalPath).Length + bytes.Length > MaxJournalBytes)
            throw new InvalidDataException("Превышен размер журнала.");
        using var file = new FileStream(attempt.JournalPath,
            FileMode.Open, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 4096, options: FileOptions.WriteThrough);
        file.Seek(0, SeekOrigin.End);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Inspect only pre-existing, fixed "reviewed" folder (no recursion).
    /// Never deletes, renames, opens for write or auto-retries pending files.
    /// Unmatched, incomplete or forged names are NOT trusted for cleanup.
    /// </summary>
    public static IReadOnlyList<Report> Inspect(
        string stagingRoot, bool labApproved = false)
    {
        if (!labApproved)
            throw new NotSupportedException("Просмотр синтетических остатков требует разрешения.");
        ValidateRoot(stagingRoot, out string reviewed);
        var reports = new List<Report>();
        var knownPending = new HashSet<string>(StringComparer.Ordinal);
        var journals = new List<string>();
        var pending = new List<string>();
        foreach (string item in Directory.EnumerateFileSystemEntries(
            reviewed, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(item);
            if (name.StartsWith(JournalPrefix, StringComparison.Ordinal))
                journals.Add(item);
            else if (name.StartsWith(PendingPrefix, StringComparison.Ordinal) &&
                     name.EndsWith(".tmp", StringComparison.Ordinal))
                pending.Add(item);
            if (journals.Count + pending.Count > MaxItems)
                throw new InvalidDataException("Слишком много лабораторных объектов для безопасного осмотра.");
        }
        foreach (string journalPath in journals.OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            string fileName = Path.GetFileName(journalPath);
            try
            {
                var entries = ReadJournal(journalPath);
                var entry = entries[^1];
                if (fileName != JournalPrefix + entry.AttemptId.ToString("N") + ".jsonl")
                    throw new InvalidDataException("Имя журнала не совпадает с его ID.");
                knownPending.Add(entry.PendingName);
                string pendingPath = Path.Combine(reviewed, entry.PendingName);
                string finalPath = Path.Combine(reviewed, entry.FinalName);
                bool hasPending = EntryExists(pendingPath);
                bool hasFinal = EntryExists(finalPath);
                // A symlink or directory masquerading as a file is never OK.
                if (hasPending)
                    EuroOfficeSyntheticReviewedSnapshot.RequireRegularPath(
                        pendingPath, expectDirectory: false);
                if (hasFinal)
                    EuroOfficeSyntheticReviewedSnapshot.RequireRegularPath(
                        finalPath, expectDirectory: false);
                Observation state =
                    hasPending && hasFinal ? Observation.PendingAfterLink :
                    hasPending ? Observation.PendingNeedsReview :
                    hasFinal && entry.Stage == "completed"
                        ? Observation.CompleteObserved :
                    hasFinal ? Observation.UncertainAfterLink :
                    entry.Stage == "completed" ? Observation.MissingFinal :
                    Observation.NoOutputObserved;
                reports.Add(new Report(entry.AttemptId, entry.PendingName,
                    entry.FinalName, entry.Stage, state));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException
                                       or JsonException or UnauthorizedAccessException
                                       or ArgumentException)
            {
                // Unknown/corrupt/linked record is a SUSPICIOUS entry only.
                reports.Add(new Report(null, "", "", "",
                    Observation.MalformedJournal));
            }
        }
        foreach (string path in pending.OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            if (!knownPending.Contains(name))
                reports.Add(new Report(null, name, "", "",
                    Observation.UnknownPending));
        }
        return reports.AsReadOnly();
    }

    private static void ValidateRoot(string stagingRoot, out string reviewed)
    {
        if (string.IsNullOrWhiteSpace(stagingRoot) ||
            !Path.IsPathFullyQualified(stagingRoot) ||
            stagingRoot.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("Только абсолютный локальный staging.");
        string root = Path.GetFullPath(stagingRoot);
        if (OperatingSystem.IsWindows() &&
            root.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("UNC/SMB недопустимы.");
        reviewed = Path.Combine(root, "reviewed");
        EuroOfficeSyntheticReviewedSnapshot.RequireRegularPath(root, true);
        EuroOfficeSyntheticReviewedSnapshot.RequireRegularPath(reviewed, true);
    }

    private static string NextStage(string stage) => stage switch
    {
        "intent" => "pending_flushed",
        "pending_flushed" => "linked",
        "linked" => "completed",
        _ => ""
    };

    private static byte[] Serialize(Attempt attempt, int sequence, string stage)
    {
        string json = JsonSerializer.Serialize(new
        {
            schema = Schema,
            attemptId = attempt.AttemptId.ToString("N"),
            sessionId = attempt.SessionId.ToString("N"),
            format = attempt.Format,
            sha256 = attempt.Sha256,
            length = attempt.Length,
            pendingName = attempt.PendingName,
            finalName = attempt.FinalName,
            sequence,
            stage
        });
        return Encoding.UTF8.GetBytes(json + "\n");
    }

    private static List<Entry> ReadJournal(string path)
    {
        EuroOfficeSyntheticReviewedSnapshot.RequireRegularPath(path, false);
        using var input = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > MaxJournalBytes)
            throw new InvalidDataException("Неверный размер синтетического журнала.");
        using var reader = new StreamReader(input,
            new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        string full = reader.ReadToEnd();
        if (!full.EndsWith('\n') || full.Contains('\r'))
            throw new InvalidDataException("Незакрытая запись журнала.");
        string[] lines = full.Split('\n', StringSplitOptions.None);
        if (lines.Length is < 2 or > 5)
            throw new InvalidDataException("Недопустимое число стадий журнала.");
        var entries = new List<Entry>();
        foreach (string line in lines[..^1])
        {
            using JsonDocument document = JsonDocument.Parse(line,
                new JsonDocumentOptions
                {
                    MaxDepth = 2, AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                });
            JsonElement obj = document.RootElement;
            if (obj.ValueKind != JsonValueKind.Object ||
                obj.EnumerateObject().Count() != 10)
                throw new InvalidDataException("Неполная запись журнала.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty p in obj.EnumerateObject())
                if (!names.Add(p.Name) || p.Name is not (
                    "schema" or "attemptId" or "sessionId" or "format" or
                    "sha256" or "length" or "pendingName" or
                    "finalName" or "sequence" or "stage"))
                    throw new InvalidDataException("Посторонние поля журнала.");
            string schema = obj.GetProperty("schema").GetString() ?? "";
            Guid id = Guid.ParseExact(obj.GetProperty("attemptId").GetString() ?? "", "N");
            Guid session = Guid.ParseExact(obj.GetProperty("sessionId").GetString() ?? "", "N");
            string format = obj.GetProperty("format").GetString() ?? "";
            string sha = obj.GetProperty("sha256").GetString() ?? "";
            long length = obj.GetProperty("length").GetInt64();
            string pending = obj.GetProperty("pendingName").GetString() ?? "";
            string final = obj.GetProperty("finalName").GetString() ?? "";
            int sequence = obj.GetProperty("sequence").GetInt32();
            string stage = obj.GetProperty("stage").GetString() ?? "";
            if (schema != Schema || id == Guid.Empty || session == Guid.Empty ||
                !ValidSha(sha) || length is <= 0 or > MaxFileBytes ||
                pending != PendingPrefix + id.ToString("N") + ".tmp" ||
                final != EuroOfficeSyntheticReviewedSnapshot.ExpectedName(
                    session, format) ||
                sequence != entries.Count + 1 ||
                stage != (sequence == 1 ? "intent" : NextStage(entries[^1].Stage)))
                throw new InvalidDataException("Противоречие в стадиях журнала.");
            var entry = new Entry(id, session, format, sha,
                length, pending, final, sequence, stage);
            if (entries.Count > 0)
            {
                Entry first = entries[0];
                if (first.AttemptId != id || first.SessionId != session ||
                    first.Sha256 != sha || first.Length != length ||
                    first.Format != format || first.PendingName != pending ||
                    first.FinalName != final)
                    throw new InvalidDataException("ID/хеш журнала изменился.");
            }
            entries.Add(entry);
        }
        return entries;
    }

    private static bool ValidSha(string sha) =>
        sha.Length == 64 && sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool EntryExists(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) return true;
        try { return new FileInfo(path).LinkTarget is not null; }
        catch (IOException) { return false; }
    }
}
