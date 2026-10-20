namespace GetPdf;

/// <summary>
/// Copy user-selected PDF data into private session storage without unbounded File.Copy.
/// Never overwrites an existing destination; remove only a partial file created by this call.
/// </summary>
internal static class PdfInputCopy
{
    internal static async Task<string> CopySafelyAsync(string source, string destination, long maxBytes,
        CancellationToken cancellationToken = default)
    {
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var sourceInfo = new FileInfo(source);
        if (!sourceInfo.Exists || sourceInfo.Length == 0 || sourceInfo.Length > maxBytes)
            throw new InvalidDataException("PDF отсутствует, пуст или превышает корпоративный лимит.");
        bool created = false;
        try
        {
            await using var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var to = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            byte[] buffer = new byte[81920];
            long totalCopied = 0;
            int read;
            while ((read = await from.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
            {
                totalCopied = checked(totalCopied + read);
                if (totalCopied > maxBytes)
                    throw new InvalidDataException("Превышен корпоративный лимит PDF во время копирования.");
                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await to.FlushAsync(cancellationToken);
            return destination;
        }
        catch
        {
            if (created)
            {
                try { File.Delete(destination); }
                catch (IOException) { /* directory cleanup will retry */ }
                catch (UnauthorizedAccessException) { /* directory cleanup will retry */ }
            }
            throw;
        }
    }
}
