using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// A durable, local working copy for an eventual native EuroOffice editor.
/// This is FILE STAGING ONLY: it does not authenticate, launch or embed CEF,
/// and never asserts that an editor has opened/saved the document.
///
/// No cleanup on Close()/crash is exposed: a working copy may contain the
/// only recoverable edits. WORM/original input is strictly read-only here.
/// </summary>
public sealed class EuroOfficeOfflineDraftSession
{
    private const long MaximumPackageBytes = 128L * 1024 * 1024;
    private const long MaximumUncompressedBytes = 512L * 1024 * 1024;
    private const int MaximumParts = 8192;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public Guid SessionId { get; }
    public string SourcePath { get; }
    public string WorkingPath { get; }
    public string RecoveryDirectory { get; }
    public string BaselineSha256 { get; }

    private EuroOfficeOfflineDraftSession(Guid sessionId, string sourcePath,
        string workingPath, string recoveryDirectory, string baselineSha256)
    {
        SessionId = sessionId;
        SourcePath = sourcePath;
        WorkingPath = workingPath;
        RecoveryDirectory = recoveryDirectory;
        BaselineSha256 = baselineSha256;
    }

    /// <summary>
    /// Start a new offline draft in an existing, local, access-controlled per-user
    /// staging root. The caller is responsible for choosing that private root.
    /// This does NOT enable the separate CEF laboratory for real documents.
    /// </summary>
    public static async Task<EuroOfficeOfflineDraftSession> CreateAsync(
        string originalPath, string privateStagingRoot,
        CancellationToken cancellationToken = default)
    {
        string source = FullPath(originalPath);
        // A draft may contain unsaved confidential edits. Never stage it to
        // an explicit UNC/network share, including Windows extended UNC.
        if (IsExplicitNetworkPath(privateStagingRoot))
            throw new NotSupportedException("Черновики EuroOffice нельзя размещать в сетевом каталоге.");
        string root = FullPath(privateStagingRoot);
        if (IsExplicitNetworkPath(root))
            throw new NotSupportedException("Черновики EuroOffice нельзя размещать в сетевом каталоге.");
        string extension = SupportedExtension(source);

        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("Частный локальный каталог черновиков не существует.");
        RequireNoLinks(root);
        RequireRegularDocument(source);
        ValidatePackage(source, extension);

        string originalHash = await FileHashAsync(source, cancellationToken);
        Guid id = Guid.NewGuid();
        string sessionDirectory = Path.Combine(root, "getdoc-eurooffice-" + id.ToString("N"));
        // Create private from the first instant on POSIX; chmod after
        // creating a world-readable directory would leave a disclosure race.
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            Directory.CreateDirectory(sessionDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else
            Directory.CreateDirectory(sessionDirectory);
        try
        {
            string workingPath = Path.Combine(sessionDirectory, "working" + extension);
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                             FileShare.Read, 65536, FileOptions.SequentialScan))
            await using (var copy = new FileStream(workingPath, FileMode.CreateNew,
                             FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                await input.CopyToAsync(copy, cancellationToken);
                copy.Flush(flushToDisk: true);
            }

            // Reject concurrent writes during snapshot. Do not expose an
            // inconsistent copy to an editor.
            string sourceHashAfterCopy = await FileHashAsync(source, cancellationToken);
            string draftHash = await FileHashAsync(workingPath, cancellationToken);
            if (!StringComparer.Ordinal.Equals(originalHash, sourceHashAfterCopy) ||
                !StringComparer.Ordinal.Equals(originalHash, draftHash))
                throw new InvalidOperationException("Файл изменился во время создания черновика.");
            ValidatePackage(workingPath, extension);

            string manifest = Path.Combine(sessionDirectory, "session.json");
            var metadata = new
            {
                schema = "getdoc-eurooffice-offline-draft/1",
                sessionId = id.ToString("N"),
                sourcePath = source,
                workingName = Path.GetFileName(workingPath),
                originalSha256 = originalHash,
                state = "unverified-native-edit"
            };
            await using (var metadataFile = new FileStream(manifest,
                FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(metadataFile, metadata,
                    cancellationToken: cancellationToken);
                metadataFile.Flush(flushToDisk: true);
            }

            return new EuroOfficeOfflineDraftSession(id, source, workingPath,
                sessionDirectory, originalHash);
        }
        catch
        {
            // This directory was freshly created and never returned to a caller.
            // Once Create succeeds, drafts are NEVER deleted automatically.
            try { Directory.Delete(sessionDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// Explicit Save As to a NEW OOXML path only. Rejects destination conflicts
    /// atomically (File.Move without overwrite), and original-source changes.
    /// Does not signal CEF Save ACK or certify Microsoft Office round-trip.
    /// The recovery copy remains available even after successful export.
    /// </summary>
    public async Task SaveAsNewCopyAsync(string destinationPath, bool userApproved,
        CancellationToken cancellationToken = default)
    {
        if (!userApproved)
            throw new NotSupportedException("Требуется явное подтверждение «Сохранить копию».");
        string destination = FullPath(destinationPath);
        string extension = SupportedExtension(destination);
        if (!string.Equals(extension, Path.GetExtension(SourcePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Сохранение копии не может менять формат OOXML.");
        if (string.Equals(destination, SourcePath, PathComparison) ||
            string.Equals(destination, WorkingPath, PathComparison))
            throw new IOException("Нельзя перезаписывать исходный или рабочий документ.");

        string targetDirectory = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(targetDirectory))
            throw new DirectoryNotFoundException("Каталог для сохранения отсутствует.");
        RequireNoLinks(targetDirectory);

        await _saveGate.WaitAsync(cancellationToken);
        string? temp = null;
        try
        {
            // A source-side version change is a conflict, not permission to
            // silently rebase. The user retains the durable working draft.
            RequireRegularDocument(SourcePath);
            if (!string.Equals(
                BaselineSha256,
                await FileHashAsync(SourcePath, cancellationToken),
                StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Исходный документ изменён другим пользователем; сохраните черновик для разбора конфликта.");

            RequireRegularDocument(WorkingPath);
            ValidatePackage(WorkingPath, extension);
            string workingHash = await FileHashAsync(WorkingPath, cancellationToken);
            temp = Path.Combine(targetDirectory,
                ".getdoc-eurooffice-save-" + Guid.NewGuid().ToString("N") + ".tmp");

            await using (var source = new FileStream(WorkingPath, FileMode.Open,
                             FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
            await using (var copy = new FileStream(temp, FileMode.CreateNew,
                             FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                await source.CopyToAsync(copy, cancellationToken);
                copy.Flush(flushToDisk: true);
            }
            if (!StringComparer.Ordinal.Equals(workingHash,
                    await FileHashAsync(temp, cancellationToken)) ||
                !StringComparer.Ordinal.Equals(workingHash,
                    await FileHashAsync(WorkingPath, cancellationToken)))
                throw new InvalidOperationException("Рабочая копия изменилась во время сохранения.");

            ValidatePackage(temp, extension);
            if (!StringComparer.Ordinal.Equals(
                BaselineSha256,
                await FileHashAsync(SourcePath, cancellationToken),
                StringComparison.Ordinal))
                throw new InvalidOperationException("Исходный документ изменился во время экспорта.");

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, destination, overwrite: false);
            temp = null;
        }
        finally
        {
            if (temp is not null)
            {
                try { File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _saveGate.Release();
        }
    }

    private static bool IsExplicitNetworkPath(string? path) =>
        path is not null &&
        (path.StartsWith(@"\\", StringComparison.Ordinal) ||
         path.StartsWith("//", StringComparison.Ordinal) ||
         path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase));

    private static string FullPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) ||
            value.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("Нужен абсолютный путь локального файла.");
        return Path.GetFullPath(value);
    }

    private static string SupportedExtension(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".docx" or ".xlsx" or ".pptx"))
            throw new NotSupportedException("Разрешены только DOCX, XLSX, PPTX без макросов.");
        return extension;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void RequireRegularDocument(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Документ отсутствует.", path);
        RequireNoLinks(path);
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumPackageBytes)
            throw new InvalidDataException("Превышен размер или документ пуст.");
    }

    private static void RequireNoLinks(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full)!;
        string cursor = root;
        foreach (string component in full[root.Length..].Split(
            Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, component);
            if (File.GetAttributes(cursor).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Символические ссылки запрещены для локального черновика.");
        }
    }

    private static async Task<string> FileHashAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 65536, FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token));
    }

    private static void ValidatePackage(string path, string extension)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaximumPackageBytes)
                throw new InvalidDataException("Некорректный размер OOXML.");
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            if (zip.Entries.Count is 0 or > MaximumParts)
                throw new InvalidDataException("Недопустимое число частей OOXML.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long unpacked = 0;
            foreach (var part in zip.Entries)
            {
                string name = part.FullName.Replace('\\', '/');
                // Explicit ZIP directory entries are valid OOXML; trailing
                // slash is allowed only for directories, never path traversal.
                bool isDirectory = name.EndsWith('/');
                string normalized = isDirectory ? name[..^1] : name;
                if (normalized.Length == 0 || normalized.StartsWith('/') ||
                    normalized.Split('/').Any(segment => segment is "." or ".." or "") ||
                    normalized.Contains(':') || !names.Add(normalized))
                    throw new InvalidDataException("Недопустимый путь или дублирование части OOXML.");
                if (name.EndsWith("/vbaProject.bin", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("vbaProject.bin", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Файлы с VBA не допускаются.");
                if (part.Length > MaximumUncompressedBytes ||
                    unpacked > MaximumUncompressedBytes - part.Length)
                    throw new InvalidDataException("Превышен предел распаковки OOXML.");
                unpacked += part.Length;
            }
            string requiredPart = extension switch
            {
                ".docx" => "word/document.xml",
                ".xlsx" => "xl/workbook.xml",
                ".pptx" => "ppt/presentation.xml",
                _ => throw new NotSupportedException()
            };
            if (!names.Contains("[Content_Types].xml") ||
                !names.Contains("_rels/.rels") || !names.Contains(requiredPart))
                throw new InvalidDataException("Пакет OOXML не соответствует расширению.");
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            throw new InvalidDataException("Не удалось проверить структуру пакета OOXML.", ex);
        }
    }
}
