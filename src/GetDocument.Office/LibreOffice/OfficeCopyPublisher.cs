namespace GetDocument.Office.LibreOffice;

/// <summary>
/// Publishes an Office copy only after the engine has finished writing into a
/// private temporary file in the destination directory. Existing destinations
/// are never overwritten; no partially exported copy is presented as complete.
/// </summary>
public static class OfficeCopyPublisher
{
    public static async Task PublishAsync(string destination,
        Func<string, Task> nativeExport)
    {
        ArgumentNullException.ThrowIfNull(nativeExport);
        string target = Path.GetFullPath(destination);
        string directory = Path.GetDirectoryName(target)!;
        string extension = Path.GetExtension(target);
        if (string.IsNullOrEmpty(extension))
            throw new ArgumentException("Для копии требуется расширение файла.", nameof(destination));
        if (File.Exists(target))
            throw new IOException("Файл уже существует. Выберите другое имя для новой копии.");
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("Папка для новой копии не найдена.");
        string staging = Path.Combine(directory,
            ".getdoc-copy-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            await nativeExport(staging);
            if (!File.Exists(staging) || new FileInfo(staging).Length == 0)
                throw new InvalidDataException("Редактор не сформировал полную копию документа.");
            // No overwrite means another process can win the destination name
            // race without losing its data. The operation stays on one volume.
            File.Move(staging, target, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(staging)) File.Delete(staging); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A failed cleanup cannot justify deleting the destination.
                // Orphaned .getdoc-copy-* files require a separate safe cleanup policy.
            }
        }
    }
}
