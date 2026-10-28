namespace GetDocument.Office.LibreOffice;

/// <summary>
/// Bounded human-entered values for Calc's native validation list. No formula or URL
/// is accepted from the UI; the bridge receives plain strings only.
/// </summary>
public static class OfficeChoiceList
{
    private const int MaxItems = 20;
    private const int MaxItemChars = 40;
    private const int MaxFormulaChars = 200;

    public static string[] ParseLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Validate(text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
    }

    public static string[] Validate(IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count is < 2 or > MaxItems)
            throw new ArgumentException("Укажите от 2 до 20 вариантов.", nameof(items));

        string[] values = new string[items.Count];
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int size = 0;
        for (int i = 0; i < items.Count; i++)
        {
            string value = (items[i] ?? "").Trim();
            if (value.Length is < 1 or > MaxItemChars ||
                value[0] is '=' or '+' or '-' or '@' ||
                value.Any(ch => char.IsControl(ch) || char.IsSurrogate(ch) ||
                                char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.Format ||
                                ch is ';' or '"'))
                throw new ArgumentException("Недопустимое значение в списке. Не используйте формулы, кавычки, ';' или управляющие символы.",
                    nameof(items));
            if (!unique.Add(value))
                throw new ArgumentException("Варианты списка не должны повторяться.", nameof(items));

            size += value.Length + 3; // quotes around each value, plus one separator
            values[i] = value;
        }

        if (size > MaxFormulaChars)
            throw new ArgumentException("Список слишком длинный для совместимого XLSX.", nameof(items));

        return values;
    }
}
