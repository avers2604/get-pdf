namespace GetDocument.Office.LibreOffice;

/// <summary>
/// Explicitly supported safe operations exposed by the GET toolbar. Never pass arbitrary
/// UNO dispatch URLs from UI to the LibreOffice bridge. Content-changing operations
/// are unavailable on read-only documents.
/// </summary>
public sealed record OfficeFeatureDefinition(string Id, string Title, string Kind, bool RequiresEdit);

public static class OfficeFeatureCatalog
{
    private static readonly OfficeFeatureDefinition[] Entries =
    [
        new("writer-toc-insert", "Вставить оглавление", "writer", true),
        new("writer-toc-update", "Обновить оглавления", "writer", true),
        new("calc-freeze-top-row", "Закрепить верхнюю строку", "calc", false),
        new("calc-freeze-first-column", "Закрепить первый столбец", "calc", false),
        new("calc-unfreeze", "Снять закрепление", "calc", false),
        new("calc-recalculate", "Пересчитать все формулы", "calc", true),
        new("calc-print-used-area", "Печатать заполненную область", "calc", true),
        new("calc-clear-print-area", "Сбросить область печати", "calc", true),
        new("calc-repeat-first-row", "Повторять первую строку при печати", "calc", true),
        new("calc-stop-repeat-rows", "Не повторять строки при печати", "calc", true),
        new("impress-duplicate-slide", "Дублировать слайд", "impress", true)
    ];

    public static IReadOnlyList<OfficeFeatureDefinition> All { get; } = Array.AsReadOnly(Entries);

    public static IReadOnlyList<OfficeFeatureDefinition> ForKind(string kind) =>
        Array.AsReadOnly(Entries.Where(feature =>
            string.Equals(feature.Kind, kind, StringComparison.Ordinal)).ToArray());

    public static OfficeFeatureDefinition Require(string kind, string featureId, bool readOnly)
    {
        OfficeFeatureDefinition? feature = Entries.FirstOrDefault(feature =>
            string.Equals(feature.Kind, kind, StringComparison.Ordinal) &&
            string.Equals(feature.Id, featureId, StringComparison.Ordinal));
        if (feature is null)
            throw new NotSupportedException("Команда недоступна для выбранного типа документа.");
        if (readOnly && feature.RequiresEdit)
            throw new InvalidOperationException("Документ открыт только для чтения.");
        return feature;
    }
}
