using System.Buffers;
using System.Security.Cryptography;

namespace GetDocument.Core.Printing;

/// <summary>
/// A print wizard owns an immutable-by-convention, per-job local PDF copy.
/// The PDF displayed in preview and sent to OS spool is the same snapshot.
/// Copies are held in a random private directory and removed on disposal.
/// </summary>
public sealed class PrintFileSnapshot : IDisposable
{
    public const long MaxBytes = 128L * 1024 * 1024;

    private readonly string _directory;
    private readonly byte[] _sha256;
    private bool _disposed;

    private PrintFileSnapshot(string directory, string path, byte[] sha256, long length)
    {
        _directory = directory;
        Path = path;
        _sha256 = sha256;
        Length = length;
    }

    public string Path { get; }
    public long Length { get; }
    public string Sha256Hex => Convert.ToHexStringLower(_sha256);

    public static string GetPrivateJobsDirectory()
    {
        string userData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(userData))
            throw new IOException("Не найден локальный каталог пользователя.");
        return System.IO.Path.Combine(userData, "GET-Document", "PrintJobs");
    }

    private static void DeleteExpiredOwnedJobs(string root)
    {
        // This directory is owned by the current OS user, not a shared spooler.
        // Never walk outside it, follow reparse points, or delete unknown content.
        foreach (string folder in Directory.EnumerateDirectories(root, "job-*",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                string name = System.IO.Path.GetFileName(folder);
                if (name.Length != 36 || !name.StartsWith("job-", StringComparison.Ordinal) ||
                    !Guid.TryParseExact(name[4..], "N", out _))
                    continue;
                var info = new DirectoryInfo(folder);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    info.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-7))
                    continue;
                string[] entries = Directory.GetFileSystemEntries(folder);
                if (entries.Length > 1 ||
                    entries.Any(path =>
                        System.IO.Path.GetFileName(path) != "document.pdf" ||
                        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                    continue;
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException) { } // A different still-active printer may own the file.
            catch (UnauthorizedAccessException) { }
        }
    }

    public static async Task<PrintFileSnapshot> CreateAsync(string sourcePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!System.IO.Path.IsPathFullyQualified(sourcePath) ||
            !string.Equals(System.IO.Path.GetExtension(sourcePath), ".pdf",
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Требуется путь к существующему PDF.", nameof(sourcePath));
        ct.ThrowIfCancellationRequested();

        string root = GetPrivateJobsDirectory();
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        DeleteExpiredOwnedJobs(root);

        string directory = System.IO.Path.Combine(root, "job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        string output = System.IO.Path.Combine(directory, "document.pdf");
        try
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length is < 5 or > MaxBytes)
                throw new InvalidDataException("PDF превышает допустимый размер или пуст.");
            var header = new byte[5];
            await source.ReadExactlyAsync(header, ct);
            if (!header.AsSpan().SequenceEqual("%PDF-"u8))
                throw new InvalidDataException("Не найден обязательный заголовок PDF.");
            source.Position = 0;

            await using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
            long total = 0;
            try
            {
                int count;
                while ((count = await source.ReadAsync(buffer, ct)) != 0)
                {
                    total += count;
                    if (total > MaxBytes)
                        throw new InvalidDataException("PDF вырос во время подготовки к печати.");
                    hasher.AppendData(buffer.AsSpan(0, count));
                    await destination.WriteAsync(buffer.AsMemory(0, count), ct);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
            await destination.FlushAsync(ct);
            if (total != source.Length || total < 5)
                throw new InvalidDataException("Исходный PDF изменился во время создания копии.");

            return new PrintFileSnapshot(directory, output, hasher.GetHashAndReset(), total);
        }
        catch
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public async Task<bool> VerifyAsync(CancellationToken ct)
    {
        if (_disposed) return false;
        try
        {
            await using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != Length || stream.Length > MaxBytes) return false;
            byte[] hash = await SHA256.HashDataAsync(stream, ct);
            return CryptographicOperations.FixedTimeEquals(_sha256, hash);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { } // May be in-flight in a Windows print spool; never overwrite.
        catch (UnauthorizedAccessException) { }
    }
}
