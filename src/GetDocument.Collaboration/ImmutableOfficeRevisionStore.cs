using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GetDocument.Collaboration;

/// <summary>
/// Immutable stored revision. FilePath is SERVER-INTERNAL and MUST NOT be
/// serialized to browser/editor clients. Access to storage is still subject
/// to enterprise ACL, document ownership and GUP-2 policy.
/// </summary>
public sealed record StoredOfficeRevision(
    string DocumentId, string RevisionId, string Extension, string Sha256,
    long Length, DateTimeOffset LastModifiedUtc, string FilePath);

internal sealed record StoredOfficeRevisionState(
    string DocumentId, string RevisionId, string Extension, string Sha256,
    long Length, DateTimeOffset CreatedUtc);

public sealed class OfficeRevisionConflictException : IOException
{
    public OfficeRevisionConflictException(string message) : base(message) { }
}

/// <summary>
/// Single-node / SMB-compatible PROTOTYPE for snapshots in a pre-existing
/// writable revision directory. No source file is ever opened for writing.
/// It relies on filesystem FileShare.None for interprocess serialization,
/// and a same-directory move for a manifest update. This is not an
/// enterprise distributed transaction, WORM audit or GUP-2 storage service.
/// </summary>
public sealed class ImmutableOfficeRevisionStore
{
    private const long MaxContentBytes = 256L * 1024 * 1024;
    private readonly string _root;

    public ImmutableOfficeRevisionStore(string existingApprovedDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(existingApprovedDirectory);
        _root = Path.GetFullPath(existingApprovedDirectory);
        if (!Directory.Exists(_root))
            throw new DirectoryNotFoundException(
                "Папка ревизий должна быть заранее создана и разрешена администратором.");
        if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Папка редакций не должна быть ссылкой.");
    }

    public async Task<StoredOfficeRevision> InitializeAsync(
        string documentId, string extension, Stream source, CancellationToken cancellationToken = default)
    {
        string key = ValidateKey(documentId);
        extension = ValidateExtension(extension);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("Исходный поток не поддерживает чтение.", nameof(source));

        // FileShare.None lock is effective across local processes and typical
        // SMB shares, but clustered storage requires a transactional server.
        using var exclusive = TakeLock(key);
        var manifest = StatePath(key);
        if (File.Exists(manifest))
        {
            var existing = ReadCurrent(documentId);
            if (existing.Extension != extension)
                throw new InvalidDataException("У документа уже зарегистрирован другой тип файла.");
            return existing;
        }
        return await CreateRevisionAsync(documentId, key, extension, source,
            replaceState: false, cancellationToken);
    }

    public async Task<StoredOfficeRevision> AppendAsync(
        string documentId, string expectedRevisionId,
        Stream edited, CancellationToken cancellationToken = default)
    {
        string key = ValidateKey(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevisionId);
        ArgumentNullException.ThrowIfNull(edited);
        if (!edited.CanRead)
            throw new ArgumentException("Входящий поток не поддерживает чтение.", nameof(edited));

        using var exclusive = TakeLock(key);
        var current = ReadCurrent(documentId);
        if (!string.Equals(current.RevisionId, expectedRevisionId, StringComparison.Ordinal))
            throw new OfficeRevisionConflictException(
                "Версия изменилась. Нельзя перезаписать результаты другого сотрудника.");
        return await CreateRevisionAsync(documentId, key, current.Extension, edited,
            replaceState: true, cancellationToken);
    }

    public StoredOfficeRevision ReadCurrent(string documentId)
    {
        string key = ValidateKey(documentId);
        string manifest = StatePath(key);
        if (!File.Exists(manifest))
            throw new FileNotFoundException("Для документа нет начальной редакции.", manifest);
        if ((File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(manifest).Length is < 16 or > 8192)
            throw new InvalidDataException("Некорректный индекс версий.");

        StoredOfficeRevisionState state;
        try
        {
            using var stream = new FileStream(manifest, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            state = JsonSerializer.Deserialize<StoredOfficeRevisionState>(
                stream) ?? throw new InvalidDataException("Пустой индекс.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Повреждён индекс версий.", ex);
        }

        if (state.DocumentId != documentId ||
            !IsRevisionId(state.RevisionId) ||
            state.Extension != ValidateExtension(state.Extension) ||
            state.Sha256 is null || state.Sha256.Length != 64 ||
            state.Length is <= 0 or > MaxContentBytes)
            throw new InvalidDataException("Индекс редакций содержит недопустимые сведения.");

        string storedFile = StoredPath(key, state.RevisionId, state.Extension);
        if (!File.Exists(storedFile) ||
            (File.GetAttributes(storedFile) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Файл зарегистрированной редакции отсутствует или заменён.");
        using var input = new FileStream(storedFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != state.Length ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(input)),
                state.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Контрольная сумма редакции не совпала.");

        return new StoredOfficeRevision(state.DocumentId, state.RevisionId,
            state.Extension, state.Sha256, state.Length, state.CreatedUtc, storedFile);
    }

    private async Task<StoredOfficeRevision> CreateRevisionAsync(
        string documentId, string key, string extension, Stream content,
        bool replaceState, CancellationToken cancellationToken)
    {
        string revisionId = Guid.NewGuid().ToString("N");
        string target = StoredPath(key, revisionId, extension);
        string temp = Path.Combine(_root, key + "-" + revisionId + ".staged");
        string stateTemp = Path.Combine(_root, key + "-" + revisionId + ".json.staged");
        bool finalCreated = false;
        bool statePublished = false;
        try
        {
            long length = 0;
            string sha;
            using (var output = new FileStream(temp, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    while (true)
                    {
                        int read = await content.ReadAsync(buffer.AsMemory(), cancellationToken);
                        if (read == 0) break;
                        length += read;
                        if (length > MaxContentBytes)
                            throw new InvalidDataException("Редакция превышает предел 256 МиБ.");
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
                if (length == 0)
                    throw new InvalidDataException("Нельзя сохранить пустую редакцию.");
                output.Flush(flushToDisk: true);
                sha = Convert.ToHexString(hash.GetHashAndReset());
            }

            File.Move(temp, target); // never overwrite immutable version bytes
            finalCreated = true;
            var timestamp = DateTimeOffset.UtcNow;
            var newState = new StoredOfficeRevisionState(
                documentId, revisionId, extension, sha, length, timestamp);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(newState);
            using (var state = new FileStream(stateTemp, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                state.Write(bytes);
                state.Flush(flushToDisk: true);
            }

            // Under the exclusive cross-process lock, replace ONLY mutable
            // state; original source and historical snapshots are never touched.
            File.Move(stateTemp, StatePath(key), overwrite: replaceState);
            statePublished = true;
            return new StoredOfficeRevision(
                documentId, revisionId, extension, sha, length, timestamp, target);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            if (File.Exists(stateTemp)) File.Delete(stateTemp);
            // Incomplete saves do not leave falsely registered revisions.
            if (finalCreated && !statePublished && File.Exists(target))
                File.Delete(target);
        }
    }

    private FileStream TakeLock(string key) =>
        new(Path.Combine(_root, key + ".lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);

    private string StatePath(string key) => Path.Combine(_root, key + ".state.json");

    private string StoredPath(string key, string revisionId, string extension) =>
        Path.Combine(_root, key + "-" + revisionId + extension);

    private static bool IsRevisionId(string value) =>
        value.Length == 32 && value.All(char.IsAsciiHexDigit);

    private static string ValidateExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        string normalized = extension.StartsWith('.') ? extension.ToLowerInvariant() :
            "." + extension.ToLowerInvariant();
        if (normalized is not (".docx" or ".xlsx" or ".pptx"))
            throw new NotSupportedException("Ревизии поддерживают DOCX/XLSX/PPTX.");
        return normalized;
    }

    private static string ValidateKey(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId) || documentId.Length > 128 ||
            documentId.Any(c => char.IsControl(c) || char.IsSurrogate(c)))
            throw new ArgumentException("Недопустимый идентификатор документа.", nameof(documentId));
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(documentId))).ToLowerInvariant();
    }
}
