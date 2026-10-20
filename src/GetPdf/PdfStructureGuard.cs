using PdfSharp.Pdf;

namespace GetPdf;

/// <summary>
/// Conservative structural safeguard, not signature validation.
/// Blocks rewriting documents whose interactive/signature/attachment structures
/// PDFsharp page-copying does not promise to preserve.
/// </summary>
internal static class PdfStructureGuard
{
    private static readonly string[] RiskyCatalogKeys =
    [
        "/AcroForm", "/Perms", "/DSS", "/Names", "/StructTreeRoot", "/Collection"
    ];

    internal static void EnsureSafeToRewrite(string filePath)
    {
        using var document = PdfSharp.Pdf.IO.PdfReader.Open(
            Path.GetFullPath(filePath), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        EnsureSafeToRewrite(document);
    }

    internal static void EnsureSafeToRewrite(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var catalog = document.Internals.Catalog;
        foreach (string key in RiskyCatalogKeys)
        {
            if (catalog.Elements.ContainsKey(key))
                throw new InvalidOperationException(
                    $"Документ содержит структуру {key}, сохранность которой не подтверждена. " +
                    "Редактирование заблокировано. Оригинал не изменён.");
        }

        foreach (var page in document.Pages)
        {
            if (page.Elements.ContainsKey("/Annots"))
                throw new InvalidOperationException(
                    "PDF содержит интерактивные аннотации или поля. " +
                    "Редактирование заблокировано до реализации безопасного редактора.");
        }

        // In ordinary PDFs signature dictionaries carry /ByteRange. This checks
        // parsed indirect objects, not raw byte patterns or an unverifiable regex.
        foreach (var item in document.Internals.GetAllObjects())
        {
            if (item is PdfDictionary dictionary && dictionary.Elements.ContainsKey("/ByteRange"))
                throw new InvalidOperationException(
                    "Обнаружена структура цифровой подписи PDF. Изменение заблокировано.");
        }
    }
}
