namespace GetDocument.Core.DocumentFiles;

/// <summary>Extension recognition only: not a security check or proof of format support.</summary>
public enum DocumentFileKind { Unsupported, Pdf, Text, Spreadsheet, Presentation, Image }
public sealed record DocumentFileType(DocumentFileKind Kind, string Extension, bool RequiresConversion)
{
    public static DocumentFileType Unsupported { get; } = new(DocumentFileKind.Unsupported, "", false);
}
public static class DocumentFileClassifier
{
    private static readonly IReadOnlyDictionary<string, (DocumentFileKind Kind, bool Convert)> Known =
        new Dictionary<string, (DocumentFileKind, bool)>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = (DocumentFileKind.Pdf, false),
            [".docx"] = (DocumentFileKind.Text, false),
            [".odt"] = (DocumentFileKind.Text, false),
            [".doc"] = (DocumentFileKind.Text, true),
            [".rtf"] = (DocumentFileKind.Text, true),
            [".txt"] = (DocumentFileKind.Text, false),
            [".md"] = (DocumentFileKind.Text, false),
            [".xlsx"] = (DocumentFileKind.Spreadsheet, false),
            [".ods"] = (DocumentFileKind.Spreadsheet, false),
            [".csv"] = (DocumentFileKind.Spreadsheet, false),
            [".xls"] = (DocumentFileKind.Spreadsheet, true),
            [".xlsb"] = (DocumentFileKind.Spreadsheet, true),
            [".pptx"] = (DocumentFileKind.Presentation, false),
            [".odp"] = (DocumentFileKind.Presentation, false),
            [".ppt"] = (DocumentFileKind.Presentation, true),
            [".pps"] = (DocumentFileKind.Presentation, true),
            [".ppsx"] = (DocumentFileKind.Presentation, false),
            [".png"] = (DocumentFileKind.Image, false),
            [".jpg"] = (DocumentFileKind.Image, false),
            [".jpeg"] = (DocumentFileKind.Image, false),
            [".tif"] = (DocumentFileKind.Image, false),
            [".tiff"] = (DocumentFileKind.Image, false),
            [".bmp"] = (DocumentFileKind.Image, false),
            [".gif"] = (DocumentFileKind.Image, false),
            [".webp"] = (DocumentFileKind.Image, false),
        };
    /// <summary>Only categorizes the file name; the file is neither opened nor trusted.</summary>
    public static DocumentFileType Classify(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return DocumentFileType.Unsupported;
        var extension = Path.GetExtension(fileName);
        return Known.TryGetValue(extension, out var item)
            ? new(item.Kind, extension.ToLowerInvariant(), item.Convert)
            : DocumentFileType.Unsupported;
    }
}
