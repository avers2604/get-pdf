using System.Security.Cryptography;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace GetDocument.Office;

/// <summary>
/// Strictly limited plain-paragraph DOCX editing. Complex documents are read-only until
/// a lossless office engine passes acceptance. Each save creates a NEW OOXML revision.
/// </summary>
public sealed record SimpleDocxSnapshot(string Text, string Sha256);

public static class SimpleDocxEditor
{
    private const int MaxSourceBytes = 16 * 1024 * 1024;
    private const int MaxCharacters = 500000;
    private const int MaxParagraphs = 1000;

    public static SimpleDocxSnapshot Load(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string source = Path.GetFullPath(sourcePath);
        var length = new FileInfo(source).Length;
        if (length is <= 0 or > MaxSourceBytes)
            throw new InvalidDataException("DOCX пуст либо превышает предел простого редактора 16 МиБ.");

        using var document = WordprocessingDocument.Open(source, false);
        var body = ValidatePlainParagraphs(document);
        var lines = body.Elements<Paragraph>().Select(p =>
            string.Concat(p.Elements<Run>().SelectMany(r => r.Elements<Text>()).Select(t => t.Text))).ToArray();
        if (lines.Length > MaxParagraphs)
            throw new NotSupportedException("Слишком много абзацев для простого редактора.");
        string text = string.Join("\n", lines);
        ValidateText(text);
        return new(text, ComputeHash(source));
    }

    /// <summary>Never overwrites source or destination; compares source bytes against loaded snapshot.</summary>
    public static void SaveNewRevision(string sourcePath, string expectedSha256,
        string destination, string editedText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        ArgumentNullException.ThrowIfNull(editedText);
        ValidateText(editedText);

        string source = Path.GetFullPath(sourcePath);
        string target = Path.GetFullPath(destination);
        if (!Path.GetExtension(source).Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(target).Equals(".docx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Источник и редакция должны быть DOCX.");
        var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(source, target, comparer))
            throw new IOException("Запись поверх исходного документа запрещена.");
        if (File.Exists(target))
            throw new IOException("Файл редакции уже существует. Перезапись запрещена.");
        var folder = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);

        // First prove this source remains a supported simple document.
        var original = Load(source);
        if (!string.Equals(original.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Документ изменён другим пользователем; перечитайте файл.");

        string temporary = Path.Combine(folder, ".get-document-revision-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            File.Copy(source, temporary);
            if (!string.Equals(ComputeHash(temporary), expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Источник изменён во время создания копии.");
            using (var document = WordprocessingDocument.Open(temporary, true))
            {
                var body = ValidatePlainParagraphs(document);
                body.RemoveAllChildren();
                foreach (var line in editedText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                    body.Append(new Paragraph(new Run(new Text(line)
                    {
                        Space = SpaceProcessingModeValues.Preserve
                    })));
                document.MainDocumentPart!.Document.Save();
            }
            // Parse the staged OOXML again before publication.
            var confirmed = Load(temporary);
            if (confirmed.Text != editedText.Replace("\r\n", "\n").Replace('\r', '\n'))
                throw new InvalidDataException("Не удалось подтвердить содержимое новой редакции.");
            // Same-folder rename without overwrite; compatible with local/SMB filesystems.
            File.Move(temporary, target);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static Body ValidatePlainParagraphs(WordprocessingDocument document)
    {
        var main = document.MainDocumentPart;
        var root = main?.Document;
        var body = root?.Body;
        if (body is null || main is null ||
            main.Parts.Any() || root!.ChildElements.Any(x => x is not Body) ||
            body.ChildElements.Any(x => x is not Paragraph) ||
            body.Elements<Paragraph>().Any(p =>
                p.ChildElements.Any(x => x is not Run) ||
                p.Elements<Run>().Any(r => r.ChildElements.Any(x => x is not Text))))
            throw new NotSupportedException(
                "Этот DOCX содержит оформление, таблицы, объекты или дополнительные части. " +
                "Для сохранения оформления требуется полноценный офисный движок.");
        return body;
    }

    private static void ValidateText(string text)
    {
        if (text.Length > MaxCharacters)
            throw new ArgumentOutOfRangeException(nameof(text), "Слишком большой текст для простого редактора.");
        if (text.Count(c => c == '\n') + 1 > MaxParagraphs)
            throw new ArgumentOutOfRangeException(nameof(text), "Максимум 1000 абзацев.");
        try { XmlConvert.VerifyXmlChars(text); }
        catch (XmlException ex) { throw new ArgumentException("Текст содержит недопустимые XML-символы.", nameof(text), ex); }
    }

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
