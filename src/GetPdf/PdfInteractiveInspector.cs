using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using System.Text;

namespace GetPdf;

/// <summary>
/// Read-only metadata inspection of an already opened PDFsharp document.
/// Never reads/displays form values, field names or annotation contents.
/// This is NOT a PDF signature validator or AcroForm editor.
/// </summary>
internal static class PdfInteractiveInspector
{
    private const int MaxVisitedItems = 20000;

    internal static PdfInteractiveSummary Inspect(string sourcePath)
    {
        using var document = PdfReader.Open(Path.GetFullPath(sourcePath), PdfDocumentOpenMode.Import);
        return Inspect(document);
    }

    internal static PdfInteractiveSummary Inspect(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var catalog = document.Internals.Catalog;
        var hasForm = catalog.Elements.ContainsKey("/AcroForm");
        var hasNames = catalog.Elements.ContainsKey("/Names");
        var hasTagged = catalog.Elements.ContainsKey("/StructTreeRoot");
        var hasCollection = catalog.Elements.ContainsKey("/Collection");
        var hasPermissions = catalog.Elements.ContainsKey("/Perms") || catalog.Elements.ContainsKey("/DSS");

        int fields = 0;
        if (hasForm)
        {
            var form = catalog.Elements.GetDictionary("/AcroForm")
                ?? throw new InvalidDataException("Структура полей PDF повреждена.");
            if (form.Elements.ContainsKey("/Fields"))
            {
                var roots = form.Elements.GetObject("/Fields") as PdfArray
                    ?? throw new InvalidDataException("В PDF некорректный список полей.");
                if (roots.Elements.Count > MaxVisitedItems)
                    throw new InvalidDataException("Слишком большое корневое дерево полей PDF.");
                var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
                var pending = new Stack<PdfItem>();
                foreach (var item in roots.Elements) pending.Push(item);
                while (pending.Count != 0)
                {
                    var dictionary = Dereference(pending.Pop()) as PdfDictionary
                        ?? throw new InvalidDataException("Поле PDF имеет неизвестную структуру.");
                    if (!seen.Add(dictionary)) continue; // cyclic /Kids, repeated indirect object
                    if (++fields > MaxVisitedItems)
                        throw new InvalidDataException("Превышен лимит узлов формы PDF.");
                    if (!dictionary.Elements.ContainsKey("/Kids")) continue;
                    var kids = dictionary.Elements.GetObject("/Kids") as PdfArray
                        ?? throw new InvalidDataException("В PDF некорректный список дочерних полей.");
                    if (kids.Elements.Count > MaxVisitedItems ||
                        pending.Count + kids.Elements.Count > MaxVisitedItems)
                        throw new InvalidDataException("Слишком большая структура полей PDF.");
                    foreach (var item in kids.Elements) pending.Push(item);
                }
            }
        }

        int annotations = 0, pagesWithAnnotations = 0;
        int widgets = 0, notes = 0, links = 0;
        foreach (var page in document.Pages)
        {
            if (!page.Elements.ContainsKey("/Annots")) continue;
            var list = page.Elements.GetObject("/Annots") as PdfArray
                ?? throw new InvalidDataException("Невозможно прочитать аннотации PDF.");
            pagesWithAnnotations++;
            if (list.Elements.Count > MaxVisitedItems - annotations)
                throw new InvalidDataException("Превышен лимит аннотаций PDF.");
            foreach (var item in list.Elements)
            {
                var annotation = Dereference(item) as PdfDictionary
                    ?? throw new InvalidDataException("В PDF некорректный объект аннотации.");
                annotations++;
                switch (annotation.Elements.GetName("/Subtype").TrimStart('/'))
                {
                    case "Widget": widgets++; break;
                    case "Text": notes++; break;
                    case "Link": links++; break;
                }
            }
        }

        int signatureRanges = 0, objects = 0;
        foreach (var item in document.Internals.GetAllObjects())
        {
            if (++objects > MaxVisitedItems)
                throw new InvalidDataException("Слишком много объектов для безопасной проверки PDF.");
            if (item is PdfDictionary dict && dict.Elements.ContainsKey("/ByteRange"))
                signatureRanges++;
        }

        bool canRewrite;
        try
        {
            PdfStructureGuard.EnsureSafeToRewrite(document);
            canRewrite = true;
        }
        catch (InvalidOperationException) { canRewrite = false; }

        return new PdfInteractiveSummary(document.PageCount, hasForm, fields,
            annotations, pagesWithAnnotations, widgets, notes, links, signatureRanges,
            hasNames, hasTagged, hasCollection, hasPermissions, canRewrite);
    }

    private static PdfItem Dereference(PdfItem item) =>
        item is PdfReference reference
            ? reference.Value ?? throw new InvalidDataException("Не найден косвенный объект PDF.")
            : item;
}

internal sealed record PdfInteractiveSummary(
    int PageCount,
    bool HasAcroForm,
    int FormFieldNodes,
    int Annotations,
    int PagesWithAnnotations,
    int WidgetAnnotations,
    int TextAnnotations,
    int LinkAnnotations,
    int SignatureByteRanges,
    bool HasNames,
    bool HasTaggedStructure,
    bool HasCollection,
    bool HasPermissionsOrValidation,
    bool CanRewriteByExistingGuard)
{
    internal string ToRussianSummary()
    {
        var result = new StringBuilder();
        result.AppendLine($"Страниц: {PageCount}");
        result.AppendLine($"AcroForm: {(HasAcroForm ? "обнаружена" : "не обнаружена")}");
        result.AppendLine($"Узлов полей (включая вложенные): {FormFieldNodes}");
        result.AppendLine($"Аннотаций: {Annotations} на {PagesWithAnnotations} стр.");
        result.AppendLine($"Из них виджетов полей: {WidgetAnnotations}; текстовых заметок: {TextAnnotations}; ссылок: {LinkAnnotations}; прочих: {Annotations - WidgetAnnotations - TextAnnotations - LinkAnnotations}.");
        result.AppendLine($"Словарей с /ByteRange (признак подписи): {SignatureByteRanges}");
        result.AppendLine($"Каталог имён: {(HasNames ? "есть" : "нет")}; теги структуры: {(HasTaggedStructure ? "есть" : "нет")}.");
        result.AppendLine($"Коллекции: {(HasCollection ? "есть" : "нет")}; структуры разрешений/LTV: {(HasPermissionsOrValidation ? "есть" : "нет")}.");
        result.AppendLine(CanRewriteByExistingGuard
            ? "Консервативная структурная проверка не обнаружила известных ограничителей записи. Это не гарантия сохранения всех свойств PDF."
            : "Структурное редактирование заблокировано; исходник доступен только для просмотра и точного копирования.");
        result.AppendLine();
        result.Append("Проверка только описывает структуру PDF. Она не проверяет действительность КЭП/электронной подписи, не извлекает значения полей и не обеспечивает сохранность аннотаций при пересохранении.");
        return result.ToString();
    }
}
