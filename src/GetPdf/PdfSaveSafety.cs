namespace GetPdf;

/// <summary>
/// PDF review is non-destructive: exports may only create a new .pdf file.
/// Recheck immediately before export; use File.Move(overwrite:false) to prevent
/// a race between this check and the final commit of the completed PDF.
/// </summary>
internal static class PdfSaveSafety
{
    public static bool TryValidateNewCopy(string destination, IEnumerable<string> inputPdfPaths,
        out string message)
    {
        message = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(destination) ||
                !Path.GetExtension(destination).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                message = "Выберите имя нового файла с расширением .pdf.";
                return false;
            }
            var full = Path.GetFullPath(destination);
            if (inputPdfPaths.Any(source =>
                    string.Equals(Path.GetFullPath(source), full, StringComparison.OrdinalIgnoreCase)))
            {
                message = "Нельзя заменять исходный PDF. Выберите другое имя для копии.";
                return false;
            }
            if (File.Exists(full) || Directory.Exists(full))
            {
                message = "Файл уже существует. Для сохранения копии укажите новое имя.";
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            message = "Некорректный путь для сохранения PDF.";
            return false;
        }
    }
}
