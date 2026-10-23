using System.Security.Cryptography;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace GetDocument.Office;

/// <summary>
/// Intentionally restricted text-only DOCX mode. Rejects all rich constructs
/// rather than destroying existing business-document formatting.
/// </summary>
public sealed record DocumentTextSnapshot(string SourcePath, string Text, string SourceSha256);

public static class SimpleDocxTextService
{
    private const long MaxInputBytes = 16L * 1024 * 1024;
    public const int MaxCharacters = 200_000;
    private const int MaxParagraphs = 2_000;

    public static DocumentTextSnapshot Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        RequireDocx(full);
        byte[] bytes = ReadBounded(full);
        using var stream = new MemoryStream(bytes, writable: false);
        using var doc = WordprocessingDocument.Open(stream, false);
        var body = GetSimpleBody(doc);
        var paragraphs = body.Elements<Paragraph>().ToArray();
        if (paragraphs.Length > MaxParagraphs)
            throw new NotSupportedException("Превышен лимит абзацев.");
        string text = string.Join("\n", paragraphs.Select(p => string.Concat(
            p.Elements<Run>().SelectMany(r => r.Elements<Text>()).Select(t => t.Text))));
        if (text.Length > MaxCharacters)
            throw new NotSupportedException("Превышен лимит текста.");
        return new DocumentTextSnapshot(full, text, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public static void SaveCopy(DocumentTextSnapshot snapshot, string text, string destination)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        string target = Path.GetFullPath(destination);
        RequireDocx(target);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(target, snapshot.SourcePath, comparison) || File.Exists(target))
            throw new IOException("Оригинал или существующий файл перезаписывать нельзя.");

        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalized.Length > MaxCharacters ||
            normalized.Count(ch => ch == '\n') >= MaxParagraphs)
            throw new ArgumentOutOfRangeException(nameof(text), "Превышен лимит текстового режима.");
        if (normalized.Any(ch => char.IsControl(ch) && ch != '\n'))
            throw new ArgumentException("Недопустимый управляющий символ.", nameof(text));

        byte[] sourceBytes = ReadBounded(snapshot.SourcePath);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(sourceBytes)), snapshot.SourceSha256,
                           StringComparison.OrdinalIgnoreCase))
            throw new IOException("Исходный документ изменился. Откройте файл заново.");
        using (var memory = new MemoryStream(sourceBytes, writable: false))
        using (var existing = WordprocessingDocument.Open(memory, false))
            _ = GetSimpleBody(existing);

        string folder = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException("Папка назначения недоступна.");
        string staging = Path.Combine(folder, ".getdocument-edit-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            using (var doc = WordprocessingDocument.Create(staging, WordprocessingDocumentType.Document))
            {
                var part = doc.AddMainDocumentPart();
                var body = new Body();
                foreach (var line in normalized.Split('\n', StringSplitOptions.None))
                    body.Append(line.Length == 0 ? new Paragraph() :
                        new Paragraph(new Run(new Text(line)
                        {
                            Space = SpaceProcessingModeValues.Preserve
                        })));
                part.Document = new Document(body);
                part.Document.Save();
            }
            File.Move(staging, target);  // Atomic-ish same-directory rename, never overwrite.
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    private static Body GetSimpleBody(WordprocessingDocument doc)
    {
        var main = doc.MainDocumentPart;
        if (main?.Document?.Body is not Body body ||
            main.Parts.Any() ||
            main.Document.ChildElements.Count != 1 ||
            body.ChildElements.Any(e => e is not Paragraph) ||
            body.Elements<Paragraph>().Any(p => p.ChildElements.Any(e => e is not Run) ||
                p.Elements<Run>().Any(r => r.ChildElements.Any(e => e is not Text))))
            throw new NotSupportedException(
                "Сложная структура документа: нужен полноценный Office-движок, простой редактор недоступен.");
        return body;
    }

    private static byte[] ReadBounded(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > MaxInputBytes)
            throw new InvalidDataException("DOCX превышает лимит 16 МиБ.");
        using var memory = new MemoryStream();
        input.CopyTo(memory);
        if (memory.Length > MaxInputBytes)
            throw new InvalidDataException("DOCX слишком большой.");
        return memory.ToArray();
    }

    private static void RequireDocx(string path)
    {
        if (!Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Простой редактор поддерживает только DOCX.", nameof(path));
    }
}
