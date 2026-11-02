namespace GetDocument.Office.LibreOffice;

/// <summary>
/// Publishes a new Office copy using a private staging name in the selected
/// destination directory. The destination is never overwritten. This works
/// on Windows and Linux but is not an SMB CAS, WORM or trusted native-editor
/// attestation: only an approved engine can establish document validity.
/// </summary>
public static class OfficeCopyPublisher
{
    /// <summary>
    /// Restrict Windows-hostile names on every platform so copies can move
    /// between Windows 10/11 and Astra/ALT/RED without ADS or DOS devices.
    /// This method accepts a single file name, not an entire path.
    /// </summary>
    public static void ValidatePortableDestinationFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.EndsWith('.', StringComparison.Ordinal) ||
            fileName.EndsWith(' ', StringComparison.Ordinal) ||
            fileName.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 ||
            fileName.Any(char.IsControl))
            throw new ArgumentException(
                "Имя копии недопустимо на Windows/Linux.", nameof(fileName));

        string stem = fileName.Split('.')[0].TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 &&
             (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
              stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             stem[3] is >= '1' and <= '9'))
            throw new ArgumentException(
                "Зарезервированное Windows-имя не допускается.", nameof(fileName));
    }

    public static async Task PublishAsync(string destination,
        Func<string, Task> nativeExport, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nativeExport);
        cancellationToken.ThrowIfCancellationRequested();

        string target = Path.GetFullPath(destination);
        ValidatePortableDestinationFileName(Path.GetFileName(target));
        string directory = Path.GetDirectoryName(target)!;
        string extension = Path.GetExtension(target);
        if (string.IsNullOrEmpty(extension))
            throw new ArgumentException("Для копии требуется расширение файла.", nameof(destination));
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("Папка для новой копии не найдена.");

        RejectLinkedDirectory(directory);
        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException("Файл уже существует. Выберите другое имя для новой копии.");

        string staging = Path.Combine(directory,
            ".getdoc-copy-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await nativeExport(staging);
            cancellationToken.ThrowIfCancellationRequested();

            // A native component must not publish a symlink/junction pointing
            // to a working document or a different data volume.
            var result = new FileInfo(staging);
            result.Refresh();
            if (!result.Exists || result.Length == 0 ||
                (result.Attributes & FileAttributes.ReparsePoint) != 0 ||
                result.LinkTarget is not null)
                throw new InvalidDataException(
                    "Редактор не сформировал обычный непустой файл копии.");

            // Check again immediately before publishing. This is a useful
            // fail-closed check, not protection against malicious TOCTOU races.
            RejectLinkedDirectory(directory);
            cancellationToken.ThrowIfCancellationRequested();

            // Same-directory atomic rename. Another writer winning the name
            // race causes failure instead of a silent overwrite on Windows
            // and Linux. No WORM/SMB/GET revision authorization is implied.
            File.Move(staging, target, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(staging) || File.GetAttributes(staging).HasFlag(FileAttributes.ReparsePoint)) File.Delete(staging); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Never remove a published copy or corporate source on cleanup.
                // Leftover staging names are handled by a separate cleanup policy.
            }
        }
    }

    private static void RejectLinkedDirectory(string directory)
    {
        for (DirectoryInfo? part = new(directory); part is not null; part = part.Parent)
        {
            part.Refresh();
            if (!part.Exists)
                throw new DirectoryNotFoundException(
                    "Папка назначения недоступна.");
            if ((part.Attributes & FileAttributes.ReparsePoint) != 0 ||
                part.LinkTarget is not null)
                throw new InvalidDataException(
                    "Сохранение через ссылку или точку повторной обработки запрещено.");
        }
    }
}
