namespace GetDocument.Office.LibreOffice;

/// <summary>
/// Explicitly supported safe operations exposed by the GET toolbar. Never pass arbitrary
/// UNO dispatch URLs from UI to the LibreOffice bridge. Content-changing operations
/// are unavailable on read-only documents.
/// </summary>
/// <summary>
/// GET toolbar command transport. Server providers require their OWN capability
/// adapter and compatibility tests before any local UNO action can be exposed.
/// These values are not a declaration that those editors are currently connected.
/// </summary>
public enum OfficeFeatureProvider
{
    LibreOfficeUno,
    EuroOfficeDocumentServer,
    OnlyOfficeLegacy,
    CollaboraWopi
}

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
        new("calc-chart-column", "Создать столбчатую диаграмму по выделению", "calc", true),
        new("calc-chart-line", "Создать линейную диаграмму по выделению", "calc", true),
        new("calc-validate-whole-nonnegative", "Разрешить только целые неотрицательные числа", "calc", true),
        new("calc-validate-decimal-nonnegative", "Разрешить только неотрицательные числа", "calc", true),
        new("calc-validate-clear", "Снять проверку данных с выделения", "calc", true),
        new("calc-validate-choice-list", "Выпадающий список по заданным вариантам", "calc", true),
        new("calc-pivot-sum", "Сводная: группировка по первой колонке, сумма второй", "calc", true),
        new("impress-duplicate-slide", "Дублировать слайд", "impress", true)
    ];

    public static IReadOnlyList<OfficeFeatureDefinition> All { get; } = Array.AsReadOnly(Entries);

    /// <summary>Legacy local-only entry point; remote integrations must use ForProvider.</summary>
    public static IReadOnlyList<OfficeFeatureDefinition> ForKind(string kind) =>
        ForProvider(kind, OfficeFeatureProvider.LibreOfficeUno);

    /// <summary>
    /// Fail closed: every currently implemented feature is a LibreOffice/UNO-only
    /// command. A signed EuroOffice session does NOT make UNO calls compatible.
    /// </summary>
    public static IReadOnlyList<OfficeFeatureDefinition> ForProvider(
        string kind, OfficeFeatureProvider provider) =>
        provider == OfficeFeatureProvider.LibreOfficeUno
            ? Array.AsReadOnly(Entries.Where(feature =>
                string.Equals(feature.Kind, kind, StringComparison.Ordinal)).ToArray())
            : Array.Empty<OfficeFeatureDefinition>();

    public static OfficeFeatureDefinition Require(string kind, string featureId, bool readOnly,
        OfficeFeatureProvider provider = OfficeFeatureProvider.LibreOfficeUno)
    {
        if (provider != OfficeFeatureProvider.LibreOfficeUno)
            throw new NotSupportedException("Эта команда ГЭТ пока не подтверждена для выбранного серверного редактора.");

        OfficeFeatureDefinition? feature = Entries.FirstOrDefault(feature =>
            string.Equals(feature.Kind, kind, StringComparison.Ordinal) &&
            string.Equals(feature.Id, featureId, StringComparison.Ordinal));
        if (feature is null)
            throw new NotSupportedException("Команда недоступна для выбранного типа документа.");
        if (readOnly && feature.RequiresEdit)
            throw new InvalidOperationException("Документ открыт только для чтения.");
        return feature;
    }

    /// <summary>
    /// Only the choice-list action accepts user parameters. No caller can smuggle
    /// arbitrary script arguments into other UNO capabilities.
    /// </summary>
    public static string[]? ValidateOptions(string featureId, IReadOnlyList<string>? options)
    {
        if (featureId == "calc-validate-choice-list")
        {
            if (options is null)
                throw new ArgumentException("Укажите варианты выпадающего списка.", nameof(options));
            return OfficeChoiceList.Validate(options);
        }
        if (options is not null)
            throw new ArgumentException("Эта операция не принимает варианты списка.", nameof(options));
        return null;
    }
}
