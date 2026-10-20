using PdfSharp.Pdf.IO;

namespace GetPdf;

/// <summary>
/// Lightweight post-write integrity gate. This does NOT establish PDF/A,
/// signature validity, annotation preservation, or PDF semantic equivalence.
/// </summary>
internal static class PdfOutputVerifier
{
    public static void Verify(string path, int expectedPageCount)
    {
        if (expectedPageCount < 1) throw new ArgumentOutOfRangeException(nameof(expectedPageCount));
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) || new FileInfo(fullPath).Length == 0)
            throw new InvalidDataException("Созданный PDF отсутствует или пуст.");

        try
        {
            using var pdf = PdfReader.Open(fullPath, PdfDocumentOpenMode.Import);
            if (pdf.PageCount != expectedPageCount)
                throw new InvalidDataException(
                    $"Ожидалось {expectedPageCount} страниц PDF, получено {pdf.PageCount}.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is InvalidDataException) throw;
            throw new InvalidDataException("Новый PDF не удалось повторно открыть для проверки.", ex);
        }
    }
}
