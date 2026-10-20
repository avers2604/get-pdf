using System.Text.Json;
using PdfSharp.Pdf;

namespace GetPdf;

/// <summary>
/// Strict request/structure gate for an opt-in .NET native annotations laboratory.
/// This policy authorizes NO change to the production WPF PDF editor.
/// Never log annotation text, page content or file paths.
/// </summary>
internal static class PdfNativeAnnotationPolicy
{
    internal const int MaxPdfPages = 100;
    internal const long MaxPdfBytes = 32L * 1024 * 1024;
    internal const int MaxRequestBytes = 64 * 1024;
    internal const int MaxAnnotations = 24;
    internal const int MaxCommentLength = 500;

    private static readonly HashSet<string> UnsafeCatalogKeys =
    [
        "/AcroForm", "/Perms", "/DSS", "/Names", "/StructTreeRoot",
        "/Collection", "/AF", "/OpenAction", "/AA", "/Outlines",
        "/OCProperties", "/OutputIntents", "/Metadata", "/PageLabels",
        "/Encrypt", "/EmbeddedFiles"
    ];

    internal static IReadOnlyList<PdfNativeAnnotationSpec> ReadRequest(byte[] raw)
    {
        if (raw is null || raw.Length is 0 or > MaxRequestBytes)
            throw new InvalidDataException("Некорректный объём запроса.");

        try
        {
            using var json = JsonDocument.Parse(raw, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 6
            });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !ExactKeys(root, "version", "annotations") ||
                !root.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out int n) || n != 1 ||
                !root.TryGetProperty("annotations", out var entries) ||
                entries.ValueKind != JsonValueKind.Array ||
                entries.GetArrayLength() is < 1 or > MaxAnnotations)
                throw new InvalidDataException("Недопустимая версия или структура запроса.");

            var result = new List<PdfNativeAnnotationSpec>(entries.GetArrayLength());
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("type", out var type) ||
                    type.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Некорректный объект аннотации.");

                string kind = type.GetString()!;
                if (kind is not ("text" or "square" or "highlight"))
                    throw new InvalidDataException("Тип аннотации не поддерживается.");
                if (!ExactKeys(entry, kind == "text"
                        ? new[] { "type", "page", "rect", "text" }
                        : new[] { "type", "page", "rect" }))
                    throw new InvalidDataException("Недопустимые свойства аннотации.");
                if (!entry.TryGetProperty("page", out var page) ||
                    page.ValueKind != JsonValueKind.Number ||
                    !page.TryGetInt32(out int pageNo) || pageNo is < 1 or > MaxPdfPages)
                    throw new InvalidDataException("Некорректный номер страницы.");
                if (!entry.TryGetProperty("rect", out var rect) ||
                    rect.ValueKind != JsonValueKind.Array || rect.GetArrayLength() != 4)
                    throw new InvalidDataException("Неверная область аннотации.");

                var values = new double[4];
                for (int i = 0; i < 4; i++)
                {
                    var item = rect[i];
                    if (item.ValueKind != JsonValueKind.Number ||
                        !item.TryGetDouble(out double d) || !double.IsFinite(d))
                        throw new InvalidDataException("Недопустимые координаты.");
                    values[i] = d;
                }
                var (x0, y0, x1, y1) = (values[0], values[1], values[2], values[3]);
                if (!(x0 >= 0 && y0 >= 0 && x1 > x0 && y1 > y0 && x1 <= 14400 && y1 <= 14400))
                    throw new InvalidDataException("Аннотация имеет недопустимый прямоугольник.");
                string comment = "";
                if (kind == "text")
                {
                    if (!entry.TryGetProperty("text", out var text) ||
                        text.ValueKind != JsonValueKind.String)
                        throw new InvalidDataException("Отсутствует текст комментария.");
                    comment = text.GetString()!;
                    if (comment.Length is < 1 or > MaxCommentLength ||
                        comment.Any(ch => char.IsControl(ch) || char.IsSurrogate(ch)))
                        throw new InvalidDataException("Недопустимое содержимое комментария.");
                }
                result.Add(new PdfNativeAnnotationSpec(kind, pageNo - 1,
                    x0, y0, x1, y1, comment));
            }
            return result;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Некорректный JSON запроса аннотаций.", ex);
        }
    }

    private static bool ExactKeys(JsonElement obj, params string[] allowed)
    {
        var props = obj.EnumerateObject().Select(x => x.Name).ToArray();
        return props.Length == allowed.Length &&
            props.Distinct(StringComparer.Ordinal).Count() == allowed.Length &&
            props.All(p => allowed.Contains(p, StringComparer.Ordinal));
    }

    internal static void ValidatePaths(string sourceArg, string outputArg)
    {
        var source = Path.GetFullPath(sourceArg);
        var output = Path.GetFullPath(outputArg);
        if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase) ||
            !source.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
            !output.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Необходимо новое имя PDF.");
    }

    internal static void EnsureSafeCatalog(PdfDocument pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        var catalog = pdf.Internals.Catalog;
        if (UnsafeCatalogKeys.Any(catalog.Elements.ContainsKey))
            throw new InvalidDataException("Неподдерживаемые структуры каталога PDF.");
    }

    internal static void ValidateDocument(
        PdfDocument pdf, IReadOnlyList<PdfNativeAnnotationSpec> operations)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(operations);
        if (pdf.PageCount is < 1 or > MaxPdfPages ||
            operations.Count is < 1 or > MaxAnnotations)
            throw new InvalidDataException("Недопустимое число страниц или операций.");

        try { PdfStructureGuard.EnsureSafeToRewrite(pdf); }
        catch (InvalidOperationException ex)
        {
            throw new InvalidDataException("Документ содержит защищённые объекты.", ex);
        }

        EnsureSafeCatalog(pdf);

        var structure = PdfInteractiveInspector.Inspect(pdf);
        if (!structure.CanRewriteByExistingGuard || structure.SignatureByteRanges != 0 ||
            structure.Annotations != 0)
            throw new InvalidDataException("Невозможно безопасно добавить аннотации.");

        foreach (var item in pdf.Internals.GetAllObjects())
            if (item is PdfDictionary obj &&
                (obj.Elements.GetName("/Type") == "/ObjStm" ||
                 obj.Elements.ContainsKey("/JavaScript")))
                throw new InvalidDataException("Сложные структуры PDF не поддерживаются.");

        foreach (var page in pdf.Pages)
        {
            if (page.Rotate != 0 || page.Elements.ContainsKey("/CropBox") ||
                page.Elements.ContainsKey("/Annots") ||
                page.Elements.ContainsKey("/AA") ||
                page.Elements.ContainsKey("/Metadata") ||
                page.Elements.ContainsKey("/PieceInfo") ||
                page.Width.Point is <= 0 or > 14400 ||
                page.Height.Point is <= 0 or > 14400)
                throw new InvalidDataException("Неподдерживаемая структура или геометрия страницы.");
        }
        foreach (var op in operations)
        {
            if (op.PageIndex < 0 || op.PageIndex >= pdf.PageCount ||
                op.X1 > pdf.Pages[op.PageIndex].Width.Point ||
                op.Y1 > pdf.Pages[op.PageIndex].Height.Point)
                throw new InvalidDataException("Аннотация выходит за пределы страницы.");
        }
    }
}

internal sealed record PdfNativeAnnotationSpec(
    string Kind, int PageIndex, double X0, double Y0, double X1, double Y1,
    string Text);
