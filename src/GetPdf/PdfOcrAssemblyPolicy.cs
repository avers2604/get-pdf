namespace GetPdf;

internal enum PdfOcrAssemblyBackend
{
    ManagedPdfSharp,
    OfflineQpdf
}

/// <summary>
/// Avoid unbounded managed PDF-object retention in the PDFsharp assembler.
/// qpdf is optional and must be provisioned locally by IT; no automatic
/// download or network lookup occurs in the closed deployment.
/// </summary>
internal static class PdfOcrAssemblyPolicy
{
    internal const int MaxManagedPages = 64;

    public static PdfOcrAssemblyBackend Select(int expectedPageCount, bool qpdfAvailable)
    {
        if (expectedPageCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedPageCount));
        if (expectedPageCount <= MaxManagedPages)
            return PdfOcrAssemblyBackend.ManagedPdfSharp;
        if (qpdfAvailable)
            return PdfOcrAssemblyBackend.OfflineQpdf;
        throw new InvalidOperationException(
            $"Для OCR документа более {MaxManagedPages} страниц необходима " +
            "одобренная ИТ-службой офлайн-утилита qpdf " +
            "(tools/qpdf/qpdf.exe). Операция остановлена до OCR, оригинал не изменён.");
    }
}
