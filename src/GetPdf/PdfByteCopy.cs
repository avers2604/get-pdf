using System.Security.Cryptography;

namespace GetPdf;

/// <summary>
/// Bit-for-bit PDF copy without opening or modifying page objects. Suitable for
/// signed, encrypted and interactive PDFs when no edits are requested.
/// It does NOT perform signature verification.
/// </summary>
internal static class PdfByteCopy
{
    public static void Copy(string sourcePath, string destinationPath, CancellationToken cancellation = default)
    {
        string source = Path.GetFullPath(sourcePath);
        string destination = Path.GetFullPath(destinationPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Выберите другое имя файла. Оригинал нельзя перезаписывать.");
        if (!File.Exists(source))
            throw new FileNotFoundException("Исходный PDF не найден.", source);
        if (!string.Equals(Path.GetExtension(source), ".pdf", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(destination), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Оба файла должны иметь расширение PDF.");

        var folder = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, ".getpdf-copy-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            PdfInputCopy.CopySafelyAsync(source, temp,
                WorkstationPolicy.Current.MaxPdfInputMiB * 1024L * 1024L, cancellation).GetAwaiter().GetResult();
            byte[] sourceHash;
            byte[] copyHash;
            using (var input = File.OpenRead(source)) sourceHash = SHA256.HashData(input);
            using (var input = File.OpenRead(temp)) copyHash = SHA256.HashData(input);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, copyHash))
                throw new InvalidDataException("Байты PDF-копии не совпадают с оригиналом.");

            cancellation.ThrowIfCancellationRequested();
            File.Move(temp, destination, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { /* best effort */ }
        }
    }
}
