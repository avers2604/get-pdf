using System.Globalization;
using System.Text;
using System.Text.Json;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace GetPdf;

/// <summary>
/// Experimental offline-only policy for the separate form-fill laboratory.
/// Strictly allows flat, unsigned, action-free, unencrypted text-only AcroForms.
/// No original PDF or secrets are logged, and this never grants production editing.
/// </summary>
internal static class PdfFormFillLabPolicy
{
    // Unicode formatting controls can reverse or hide visible field values.
    // Keep the supported text-only forms predictable in all PDF viewers.
    internal static bool HasUnsafeText(string text) => text.Any(c =>
        char.IsControl(c) || char.IsSurrogate(c) ||
        char.GetUnicodeCategory(c) == UnicodeCategory.Format);

    internal const int MaxFields = 32;
    internal const int MaxRequestBytes = 65536;
    internal const int MaxValueLength = 512;
    internal const int MaxPdfPages = 100;
    internal const long MaxPdfBytes = 32L * 1024 * 1024;

    internal static IReadOnlyDictionary<string, string> ReadRequest(byte[] utf8)
    {
        if (utf8 is null || utf8.Length is 0 or > MaxRequestBytes)
            throw new InvalidDataException("Недопустимый размер запроса формы.");
        try
        {
            using var json = JsonDocument.Parse(utf8, new JsonDocumentOptions
            {
                AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Ожидался JSON-объект.");
            var rootProps = root.EnumerateObject().ToArray();
            if (rootProps.Length != 2 || rootProps.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != 2 ||
                !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out int n) || n != 1 ||
                !root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Версия или формат запроса формы недопустим.");
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in fields.EnumerateObject())
            {
                string name = property.Name;
                if (name.Length is 0 or > 120 || HasUnsafeText(name) ||
                    !result.TryAdd(name, ""))
                    throw new InvalidDataException("Некорректные или повторяющиеся имена полей.");
                if (result.Count > MaxFields || property.Value.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Недопустимое число или тип значений.");
                string value = property.Value.GetString()!;
                if (value.Length > MaxValueLength || HasUnsafeText(value))
                    throw new InvalidDataException("Недопустимое содержимое текстового поля.");
                // Preserve Unicode, including Cyrillic; do not transliterate or normalize silently.
                result[name] = value;
            }
            if (result.Count == 0) throw new InvalidDataException("Пустой запрос.");
            return result;
        }
        catch (JsonException ex) { throw new InvalidDataException("Повреждённый JSON запроса формы.", ex); }
    }

    internal static void ValidateDocument(PdfDocument document, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0 || values.Count > MaxFields || document.PageCount is < 1 or > MaxPdfPages)
            throw new InvalidDataException("Недопустимая форма или число страниц.");

        // This scanner conservatively recognizes known catalog and indirect signature hazards.
        var structure = PdfInteractiveInspector.Inspect(document);
        var form = PdfFormPreflight.Inspect(document, structure);
        if (!form.HasAcroForm || form.HasXfa || form.HasNeedAppearances ||
            form.HasCalculationOrder || form.HasCatalogActions || form.FieldsWithActions > 0 ||
            form.SignatureFields > 0 || form.PasswordTextFields > 0 ||
            form.ReadOnlyFields > 0 || form.UnknownFields > 0 ||
            form.TextFields != form.FieldCount ||
            structure.SignatureByteRanges != 0 || structure.HasPermissionsOrValidation ||
            structure.HasNames || structure.HasCollection || structure.HasTaggedStructure ||
            structure.TextAnnotations > 0 || structure.LinkAnnotations > 0 ||
            structure.Annotations != structure.WidgetAnnotations)
            throw new InvalidDataException("Форма содержит неподдерживаемые или защищённые структуры.");

        var formDictionary = document.Internals.Catalog.Elements.GetDictionary("/AcroForm")
            ?? throw new InvalidDataException("Нет словаря AcroForm.");
        var roots = formDictionary.Elements.GetObject("/Fields") as PdfArray
            ?? throw new InvalidDataException("Некорректные поля AcroForm.");
        // The first trial deliberately excludes inherited /FT, nested /Kids and field widgets.
        if (roots.Elements.Count > MaxFields || roots.Elements.Count != form.FieldCount)
            throw new InvalidDataException("Экспериментальный модуль поддерживает только плоские формы.");
        var allowed = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in roots.Elements)
        {
            var field = item is PdfReference reference ? reference.Value as PdfDictionary : item as PdfDictionary;
            if (field is null || field.Elements.GetName("/FT") != "/Tx" ||
                field.Elements.ContainsKey("/Kids") || field.Elements.ContainsKey("/AA") ||
                field.Elements.ContainsKey("/A") || field.Elements.ContainsKey("/Lock") ||
                field.Elements.ContainsKey("/SV"))
                throw new InvalidDataException("Неподдерживаемый формат поля.");
            string name = field.Elements.GetString("/T");
            if (string.IsNullOrWhiteSpace(name) || name.Length > 120 ||
                HasUnsafeText(name) ||
                !allowed.TryAdd(name, field.Elements.ContainsKey("/MaxLen")
                    ? field.Elements.GetInteger("/MaxLen") : MaxValueLength))
                throw new InvalidDataException("Отсутствует уникальное имя текстового поля.");
            int flags = field.Elements.GetInteger("/Ff");
            // Only the Required bit is allowed. ReadOnly, RichText, multiline, password,
            // do-not-scroll, spellcheck or dynamic controls need a dedicated renderer.
            if ((flags & ~2) != 0 || allowed[name] < 0)
                throw new InvalidDataException("Неподдерживаемые ограничения поля.");
        }
        foreach (var (name, value) in values)
        {
            if (!allowed.TryGetValue(name, out int limit) || value.Length > limit)
                throw new InvalidDataException("Неизвестное поле либо превышена длина.");
        }
    }
}
