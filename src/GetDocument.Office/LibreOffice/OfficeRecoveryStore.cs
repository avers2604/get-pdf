using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
        Directory.CreateDirectory(folder);
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
            File.Move(temporaryCopy, finalCopy);
            var manifest = new RecoveryManifest(source, DateTimeOffset.UtcNow, sha);
            await File.WriteAllTextAsync(temporaryManifest, JsonSerializer.Serialize(manifest), cancellationToken);
            // The manifest is the commit marker. Interrupted exports never become recoverable.
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

    public OfficeRecoveryCopy? FindLatest(string sourcePath)
    {
        string source = CanonicalSource(sourcePath);
        string extension = AllowedExtension(source);
        string folder = FolderFor(source);
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
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
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

    private static RecoveryManifest? ReadManifest(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 2048) return null;
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
        }
        if (!parts.Contains("[Content_Types].xml") || !parts.Contains(mainPart))
            throw new InvalidDataException("В аварийной копии отсутствуют основные OOXML-части.");
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
