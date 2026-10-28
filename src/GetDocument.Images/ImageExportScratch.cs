using System.Text;

namespace GetDocument.Images;

/// <summary>
/// Private, exclusively leased JPEG/PNG staging on the output volume.
/// An interrupted export is never published. The next export in the same
/// folder may reclaim only owned, inactive scratch older than 24 hours.
/// No recursive deletion, global temp scan, or cleanup by extension alone.
/// </summary>
internal sealed class ImageExportScratch : IDisposable
{
    internal const string LeaseFileName = ".get-document-image-owner";
    internal const string LeaseMarker = "get-document-image-export-v1";
    private const string DirectoryPrefix = ".get-document-image-";
    private static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(24);
    private readonly FileStream _lease;
    private bool _disposed;

    private ImageExportScratch(string directory, string extension, FileStream lease)
    {
        DirectoryPath = directory;
        StagingPath = Path.Combine(directory, "stage" + extension);
        _lease = lease;
    }

    internal string DirectoryPath { get; }
    internal string StagingPath { get; }

    internal static ImageExportScratch Create(string outputFolder, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        string normalizedExtension = NormalizeExtension(extension);
        string parent = Path.GetFullPath(outputFolder);
        if (!Directory.Exists(parent) ||
            (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Папка назначения недоступна или является ссылкой.");

        CleanupAbandoned(parent, DefaultRetention);
        string directory = Path.Combine(parent,
            DirectoryPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var lease = new FileStream(Path.Combine(directory, LeaseFileName),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            try
            {
                byte[] marker = Encoding.ASCII.GetBytes(LeaseMarker);
                lease.Write(marker);
                lease.Flush(flushToDisk: true);
                return new ImageExportScratch(directory, normalizedExtension, lease);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        catch
        {
            // Never recursively remove a partially created directory: if another
            // entry appeared, leave it untouched for operator investigation.
            try { TryDeleteOwnedDirectory(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { /* Conservative fail-closed cleanup. */ }
            throw;
        }
    }

    internal static void CleanupAbandoned(string outputFolder, TimeSpan minAge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        if (minAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minAge));
        try
        {
            string parent = Path.GetFullPath(outputFolder);
            if (!Directory.Exists(parent) ||
                (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                return;
            DateTime cutoff = DateTime.UtcNow - minAge;
            foreach (string directory in Directory.EnumerateDirectories(parent,
                         DirectoryPrefix + "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (!HasOwnedName(Path.GetFileName(directory)) ||
                        (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        continue;

                    string marker = Path.Combine(directory, LeaseFileName);
                    if (!File.Exists(marker) ||
                        (File.GetAttributes(marker) &
                         (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                        File.GetLastWriteTimeUtc(marker) > cutoff)
                        continue;

                    TryDeleteOwnedDirectory(directory, cutoff);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Locked files, unsupported volumes and races stay on disk.
                    // No fallback to broad recursive deletion.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best-effort and must not block a new export.
        }
    }

    private static string NormalizeExtension(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".png" => ".png",
            ".jpg" or ".jpeg" => ".jpg",
            _ => throw new NotSupportedException("Временный экспорт допускает только PNG/JPEG.")
        };

    private static bool HasOwnedName(string? name) =>
        name is not null &&
        name.StartsWith(DirectoryPrefix, StringComparison.Ordinal) &&
        name.Length == DirectoryPrefix.Length + 32 &&
        Guid.TryParseExact(name[DirectoryPrefix.Length..], "N", out _);

    private static void TryDeleteOwnedDirectory(string directory)
    {
        if (!HasOwnedName(Path.GetFileName(directory)) ||
            (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            return;

        string marker = Path.Combine(directory, LeaseFileName);
        if (!File.Exists(marker) ||
            (File.GetAttributes(marker) &
             (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            return;

        // A live export holds this owner marker with FileShare.None.
        using (var lease = new FileStream(marker,
                   FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            byte[] expected = Encoding.ASCII.GetBytes(LeaseMarker);
            byte[] actual = new byte[expected.Length];
            if (lease.Length != expected.Length) return;
            lease.ReadExactly(actual);
            if (!actual.AsSpan().SequenceEqual(expected)) return;

            string[] entries = Directory.EnumerateFileSystemEntries(
                directory, "*", SearchOption.TopDirectoryOnly).ToArray();
            if (entries.Length is < 1 or > 2) return;
            string? stagedFile = null;
            bool foundMarker = false;
            foreach (string entry in entries)
            {
                string name = Path.GetFileName(entry);
                if (name is not (LeaseFileName or "stage.png" or "stage.jpg") ||
                    (File.GetAttributes(entry) &
                     (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    return;
                if (name == LeaseFileName) foundMarker = true;
                else
                {
                    if (stagedFile is not null) return;
                    stagedFile = entry;
                }
            }
            if (!foundMarker) return;
            // A stage modified after the retention cutoff could be a resumed
            // writer or unrelated user content: preserve it rather than guess.
            if (oldestAllowedUtc is { } cutoff &&
                (File.GetLastWriteTimeUtc(marker) > cutoff ||
                 (stagedFile is not null && File.GetLastWriteTimeUtc(stagedFile) > cutoff)))
                return;
            if (stagedFile is not null) File.Delete(stagedFile);
        }

        // Non-recursive operations refuse unknown children even if one appears
        // between validation and removal.
        File.Delete(marker);
        Directory.Delete(directory);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _lease.Dispose(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Do not turn cleanup failure into a failed publication.
        }
        try
        {
            if (Directory.Exists(DirectoryPath))
                TryDeleteOwnedDirectory(DirectoryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never attempt force-removal if a foreign entry or lease is present.
        }
    }
}
