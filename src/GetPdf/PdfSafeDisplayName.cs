using System.Globalization;

namespace GetPdf;

/// <summary>
/// Removes invisible bidirectional/format controls from untrusted PDF labels.
/// Display-only: never change the filename/path used by storage or audit.
/// </summary>
internal static class PdfSafeDisplayName
{
    internal static string Normalize(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return "Документ PDF";
        var visible = new string(label.Where(c =>
            !char.IsControl(c) && !char.IsSurrogate(c) &&
            char.GetUnicodeCategory(c) != UnicodeCategory.Format)
            .Take(160).ToArray()).Trim();
        return visible.Length == 0 ? "Документ PDF" : visible;
    }
}
