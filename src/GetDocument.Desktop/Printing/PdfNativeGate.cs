namespace GetDocument.Desktop.Printing;

/// <summary>Serialize PDFium access within the Avalonia process across
/// preview and print wizard to avoid concurrent native decoder calls.</summary>
internal static class PdfNativeGate
{
    public static readonly SemaphoreSlim Instance = new(1, 1);
}
