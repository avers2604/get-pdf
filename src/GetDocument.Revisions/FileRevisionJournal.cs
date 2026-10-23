using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace GetDocument.Revisions;

public sealed record RevisionRecord(
    Guid Id, string DocumentKey, string RevisionPath, string Sha256,
    DateTimeOffset CreatedUtc, string Label);
public sealed record RevisionComment(
    Guid Id, string DocumentKey, Guid RevisionId, string Author,
    string Body, DateTimeOffset CreatedUtc);
public sealed record RevisionJournalSnapshot(
    IReadOnlyList<RevisionRecord> Revisions, IReadOnlyList<RevisionComment> Comments);

/// <summary>
/// Append-only EVENT FILES in a user-selected existing metadata directory.
/// Files themselves are NOT copied, locked, made WORM or altered here.
/// The metadata folder must have appropriate OS/SMB ACLs.
/// This is a lightweight LOCAL prototype, NOT a trusted audit ledger or digital signature.
/// </summary>
public sealed class FileRevisionJournal
{
    private const int MaxEventsPerDocument = 500;
    private const int MaxEventBytes = 16_384;
    private const long MaxReferencedBytes = 256L * 1024 * 1024;
    private readonly string _metadataRoot;

    private sealed record StoredEvent(
        string DocumentKey, string Kind, Guid Id, Guid RevisionId,
        string? RevisionPath, string? Sha256, string? Label,
        string? Author, string? Body, DateTimeOffset CreatedUtc);

    public FileRevisionJournal(string metadataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataDirectory);
        _metadataRoot = Path.GetFullPath(metadataDirectory);
        if (!Directory.Exists(_metadataRoot))
            throw new DirectoryNotFoundException("Папка журнала не существует или недоступна.");
        EnsurePlainDirectory();
    }

    public RevisionRecord RecordRevision(string originalPath, string revisionPath, string label)
    {
        string source = NormalizeExistingFile(originalPath);
        string revision = NormalizeExistingFile(revisionPath);
        if (SamePath(source, revision))
            throw new IOException("Оригинал не может быть записан как собственная новая редакция.");
        label = ValidateHumanText(label, 100, multiline: false);

        var key = DocumentKey(source);
        var snapshot = Read(source);
        if (snapshot.Revisions.Any(r => SamePath(r.RevisionPath, revision)))
            throw new IOException("Путь редакции уже зарегистрирован. Создайте отдельный файл.");

        string sha = HashFile(revision);
        var record = new RevisionRecord(Guid.NewGuid(), key, revision, sha,
            DateTimeOffset.UtcNow, label);
        WriteEvent(new StoredEvent(key, "revision", record.Id, Guid.Empty, record.RevisionPath,
            record.Sha256, record.Label, null, null, record.CreatedUtc));
        return record;
    }

    public RevisionComment AddComment(string originalPath, Guid revisionId, string author, string body)
    {
        string source = NormalizeExistingFile(originalPath);
        author = ValidateHumanText(author, 100, multiline: false);
        body = ValidateHumanText(body, 2000, multiline: true);
        var snapshot = Read(source);
        if (revisionId != Guid.Empty && !snapshot.Revisions.Any(r => r.Id == revisionId))
            throw new ArgumentException("Редакция не принадлежит выбранному документу.", nameof(revisionId));
        var record = new RevisionComment(Guid.NewGuid(), DocumentKey(source), revisionId,
            author, body, DateTimeOffset.UtcNow);
        WriteEvent(new StoredEvent(record.DocumentKey, "comment", record.Id, record.RevisionId,
            null, null, null, record.Author, record.Body, record.CreatedUtc));
        return record;
    }

    public RevisionJournalSnapshot Read(string originalPath)
    {
        string source = Path.GetFullPath(originalPath);
        string key = DocumentKey(source);
        EnsurePlainDirectory();
        var versions = new List<RevisionRecord>();
        var notes = new List<RevisionComment>();
        int count = 0;
        foreach (string path in Directory.EnumerateFiles(_metadataRoot,
                     key + "-*.json", SearchOption.TopDirectoryOnly))
        {
            if (++count > MaxEventsPerDocument)
                throw new InvalidDataException("Превышен лимит 500 записей на документ.");
            var info = new FileInfo(path);
            if (info.Length is < 1 or > MaxEventBytes ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Некорректный файл журнала.");
            StoredEvent? e;
            try
            {
                e = JsonSerializer.Deserialize<StoredEvent>(File.ReadAllBytes(path));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Журнал содержит повреждённую JSON-запись.", ex);
            }
            if (e is null || e.DocumentKey != key || e.Id == Guid.Empty)
                throw new InvalidDataException("Журнал содержит недопустимую запись.");
            if (e.Kind == "revision")
            {
                if (string.IsNullOrWhiteSpace(e.RevisionPath) ||
                    string.IsNullOrWhiteSpace(e.Sha256) ||
                    e.Sha256.Length != 64 || string.IsNullOrWhiteSpace(e.Label))
                    throw new InvalidDataException("Запись редакции неполная.");
                versions.Add(new RevisionRecord(e.Id, e.DocumentKey, e.RevisionPath,
                    e.Sha256, e.CreatedUtc, e.Label));
            }
            else if (e.Kind == "comment")
            {
                if (string.IsNullOrWhiteSpace(e.Author) || string.IsNullOrWhiteSpace(e.Body))
                    throw new InvalidDataException("Комментарий в журнале неполный.");
                notes.Add(new RevisionComment(e.Id, e.DocumentKey, e.RevisionId,
                    e.Author, e.Body, e.CreatedUtc));
            }
            else throw new InvalidDataException("Неизвестный тип записи журнала.");
        }
        var revisionIds = versions.Select(v => v.Id).ToHashSet();
        if (notes.Any(n => n.RevisionId != Guid.Empty && !revisionIds.Contains(n.RevisionId)))
            throw new InvalidDataException("Комментарий ссылается на отсутствующую редакцию.");
        return new RevisionJournalSnapshot(
            versions.OrderBy(x => x.CreatedUtc).ThenBy(x => x.Id).ToArray(),
            notes.OrderBy(x => x.CreatedUtc).ThenBy(x => x.Id).ToArray());
    }

    public bool Verify(RevisionRecord revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        try
        {
            return string.Equals(HashFile(revision.RevisionPath), revision.Sha256,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private void WriteEvent(StoredEvent e)
    {
        EnsurePlainDirectory();
        string final = Path.Combine(_metadataRoot, e.DocumentKey + "-" + e.Id.ToString("N") + ".json");
        string staging = Path.Combine(_metadataRoot, ".get-document-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(e);
            if (json.Length > MaxEventBytes)
                throw new InvalidDataException("Событие слишком велико.");
            using (var output = new FileStream(staging, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, bufferSize: 4096, options: FileOptions.WriteThrough))
            {
                output.Write(json);
                output.Flush(flushToDisk: true);
            }
            File.Move(staging, final); // NEVER overwrite another user's event.
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private void EnsurePlainDirectory()
    {
        var attrs = File.GetAttributes(_metadataRoot);
        if ((attrs & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Нельзя хранить журнал в каталоге-ссылке.");
    }

    private static string NormalizeExistingFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalized = Path.GetFullPath(path);
        if (!File.Exists(normalized))
            throw new FileNotFoundException("Документ или редакция недоступны.", normalized);
        if ((File.GetAttributes(normalized) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Регистрация символьных ссылок не поддерживается.");
        return normalized;
    }

    private static string DocumentKey(string path)
    {
        // The identity is path-based, not filesystem-stable; mapped SMB/CIFS paths
        // on different operating systems can produce different document keys.
        string full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
            full = full.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)));
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string HashFile(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > MaxReferencedBytes)
            throw new InvalidDataException("Файл пуст или превышает лимит контрольной суммы 256 МиБ.");
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static string ValidateHumanText(string? text, int maxLength, bool multiline)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength)
            throw new ArgumentException("Поле пусто или превышает допустимую длину.");
        foreach (char ch in text)
        {
            if (char.IsSurrogate(ch) || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format ||
                (char.IsControl(ch) && !(multiline && ch == '\n')))
                throw new ArgumentException("Недопустимые управляющие или двунаправленные символы.");
        }
        return text.Trim();
    }
}
