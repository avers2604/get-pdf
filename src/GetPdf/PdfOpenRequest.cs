namespace GetPdf;

/// <summary>Only existing local/UNC PDF files may be passed to the PDF reader via shell association.</summary>
internal static class PdfOpenRequest
{
    public static bool TryParse(IReadOnlyList<string> args, out string? absolutePath)
    {
        absolutePath = null;
        if (args.Count != 1 || string.IsNullOrWhiteSpace(args[0])) return false;
        try
        {
            var path = Path.GetFullPath(args[0]);
            if (!Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path)) return false;
            absolutePath = path;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        { return false; }
    }
}
