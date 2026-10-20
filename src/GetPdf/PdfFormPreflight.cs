using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using System.Text;

namespace GetPdf;

/// <summary>
/// Bounded, read-only AcroForm compatibility inventory.
/// Field names, values, options, appearance bytes and annotation contents are
/// intentionally never inspected or logged. This class cannot fill any fields.
/// </summary>
internal static class PdfFormPreflight
{
    private const int MaxNodes = 4096;
    private const int MaxDepth = 64;

    internal static PdfFormPreflightSummary Inspect(
        PdfDocument document, PdfInteractiveSummary? structure = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        structure ??= PdfInteractiveInspector.Inspect(document);
        var catalog = document.Internals.Catalog;
        if (!catalog.Elements.ContainsKey("/AcroForm"))
            return new PdfFormPreflightSummary(false,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                false, false, false, false);

        var form = catalog.Elements.GetDictionary("/AcroForm")
            ?? throw new InvalidDataException("Словарь AcroForm повреждён.");
        var roots = form.Elements.GetObject("/Fields") as PdfArray
            ?? throw new InvalidDataException("В форме отсутствует корректный список полей.");
        if (roots.Elements.Count > MaxNodes)
            throw new InvalidDataException("Слишком много полей формы.");

        int count = 0, textFields = 0, choices = 0, checkboxes = 0, radios = 0;
        int buttons = 0, signatures = 0, unknown = 0, required = 0, readOnly = 0;
        int multiline = 0, passwords = 0, maxLength = 0, actions = 0;
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var queue = new Stack<(PdfItem Item, string Type, int Flags, int Depth)>();
        foreach (var root in roots.Elements)
            queue.Push((root, "", 0, 0));

        while (queue.Count != 0)
        {
            var (item, inheritedType, inheritedFlags, depth) = queue.Pop();
            if (depth > MaxDepth)
                throw new InvalidDataException("Превышена глубина дерева полей PDF.");
            var field = Dereference(item) as PdfDictionary
                ?? throw new InvalidDataException("Некорректный узел поля PDF.");
            if (!visited.Add(field))
                throw new InvalidDataException("Зацикленное или повторно используемое дерево полей PDF.");
            if (visited.Count > MaxNodes)
                throw new InvalidDataException("Превышен лимит полей PDF.");

            var type = field.Elements.ContainsKey("/FT")
                ? field.Elements.GetName("/FT") : inheritedType;
            var flags = field.Elements.ContainsKey("/Ff")
                ? field.Elements.GetInteger("/Ff") : inheritedFlags;
            bool hasAction = field.Elements.ContainsKey("/AA") || field.Elements.ContainsKey("/A");

            if (field.Elements.ContainsKey("/Kids"))
            {
                var kids = field.Elements.GetObject("/Kids") as PdfArray
                    ?? throw new InvalidDataException("Некорректный список дочерних полей PDF.");
                if (kids.Elements.Count > MaxNodes || kids.Elements.Count + queue.Count > MaxNodes)
                    throw new InvalidDataException("Слишком много дочерних полей PDF.");
                if (kids.Elements.Count == 0)
                    throw new InvalidDataException("Пустой список дочерних полей PDF.");
                int widgets = 0;
                foreach (var child in kids.Elements)
                {
                    var entry = Dereference(child) as PdfDictionary
                        ?? throw new InvalidDataException("Некорректное дочернее поле PDF.");
                    if (entry.Elements.GetName("/Subtype") == "/Widget")
                    {
                        widgets++;
                        if (entry.Elements.ContainsKey("/AA") || entry.Elements.ContainsKey("/A"))
                            hasAction = true;
                    }
                }
                if (widgets != 0 && widgets != kids.Elements.Count)
                    throw new InvalidDataException("Смешанное дерево полей и виджетов PDF.");
                if (widgets == 0)
                {
                    foreach (var child in kids.Elements)
                        queue.Push((child, type, flags, depth + 1));
                    continue;
                }
                // Widget children are visual representations of one terminal field.
                // They must not be counted as separate user-editable fields.
            }

            if (string.IsNullOrEmpty(type))
                throw new InvalidDataException("Тип поля AcroForm не определён.");
            if (++count > MaxNodes)
                throw new InvalidDataException("Превышено число полей AcroForm.");
            if ((flags & 1) != 0) readOnly++;
            if ((flags & 2) != 0) required++;
            if (hasAction) actions++;
            switch (type)
            {
                case "/Tx":
                    textFields++;
                    if ((flags & (1 << 12)) != 0) multiline++;
                    if ((flags & (1 << 13)) != 0) passwords++;
                    if (field.Elements.ContainsKey("/MaxLen"))
                    {
                        int limit = field.Elements.GetInteger("/MaxLen");
                        if (limit < 0)
                            throw new InvalidDataException("Отрицательная максимальная длина поля PDF.");
                        if (limit > 0) maxLength++;
                    }
                    break;
                case "/Ch":
                    choices++;
                    break;
                case "/Btn":
                    if ((flags & (1 << 16)) != 0) buttons++;
                    else if ((flags & (1 << 15)) != 0) radios++;
                    else checkboxes++;
                    break;
                case "/Sig":
                    signatures++;
                    break;
                default:
                    unknown++;
                    break;
            }
        }

        bool xfa = form.Elements.ContainsKey("/XFA");
        bool calculate = form.Elements.ContainsKey("/CO");
        bool appearanceRebuild = form.Elements.ContainsKey("/NeedAppearances");
        bool documentActions = catalog.Elements.ContainsKey("/OpenAction") ||
            catalog.Elements.ContainsKey("/AA");
        return new PdfFormPreflightSummary(true, count, textFields, choices,
            checkboxes, radios, buttons, signatures, unknown, required, readOnly,
            multiline, passwords, maxLength, actions, xfa, calculate,
            appearanceRebuild, documentActions);
    }

    private static PdfItem Dereference(PdfItem item) =>
        item is PdfReference reference
            ? reference.Value ?? throw new InvalidDataException("Косвенный объект PDF отсутствует.")
            : item;
}

internal sealed record PdfFormPreflightSummary(
    bool HasAcroForm,
    int FieldCount,
    int TextFields,
    int ChoiceFields,
    int CheckBoxes,
    int RadioButtons,
    int PushButtons,
    int SignatureFields,
    int UnknownFields,
    int RequiredFields,
    int ReadOnlyFields,
    int MultilineTextFields,
    int PasswordTextFields,
    int FieldsWithMaxLength,
    int FieldsWithActions,
    bool HasXfa,
    bool HasCalculationOrder,
    bool HasNeedAppearances,
    bool HasCatalogActions)
{
    // PDFsharp 6.2.4 cannot guarantee correct AcroForm appearance regeneration.
    // Explicitly avoid enabling writes based on this inspection alone.
    internal bool FillSupported => false;

    internal string ToRussianSummary()
    {
        var result = new StringBuilder();
        result.AppendLine("Поля формы AcroForm (только анализ, без персональных значений)");
        if (!HasAcroForm)
            return "Форма AcroForm не обнаружена. Заполнение недоступно.";
        result.AppendLine($"Полей: {FieldCount}; текстовых: {TextFields}; выпадающих/списков: {ChoiceFields}.");
        result.AppendLine($"Флажков: {CheckBoxes}; переключателей: {RadioButtons}; кнопок: {PushButtons}; полей подписи: {SignatureFields}; неизвестных: {UnknownFields}.");
        result.AppendLine($"Обязательных: {RequiredFields}; только для чтения: {ReadOnlyFields}; многострочных: {MultilineTextFields}; парольных: {PasswordTextFields}.");
        result.AppendLine($"С ограничением длины: {FieldsWithMaxLength}; с действиями: {FieldsWithActions}.");
        result.AppendLine($"XFA: {(HasXfa ? "есть" : "нет")}; порядок вычислений: {(HasCalculationOrder ? "есть" : "нет")}; запрос генерации внешнего вида: {(HasNeedAppearances ? "есть" : "нет")}; действия документа: {(HasCatalogActions ? "есть" : "нет")}.");
        result.Append("Заполнение в GET-PDF пока не включено: создание визуального представления полей и сохранность исходной формы не прошли проверку. Оригинал не изменяется.");
        return result.ToString();
    }
}
