using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Scriban;
using Scriban.Runtime;

namespace GetDocument.Office.Templates;

/// <summary>Built-in pilot template IDs. Not an assertion that GЭТ has approved these forms.</summary>
public enum CorporateTemplateId { ServiceMemo, Act, Letter }

public sealed record CorporateTemplateInfo(
    CorporateTemplateId Id, string Name, string Description);

public sealed record CorporateTemplateData(
    string Department,
    string Author,
    string Recipient,
    string Subject,
    string Content,
    string DocumentNumber,
    DateOnly DocumentDate);

/// <summary>
/// A bounded, local, deterministic generator. Only source-controlled templates are executed;
/// user-provided data is string-valued, never evaluated as Scriban code.
/// No remote includes, arbitrary template files, reflection imports, I/O or remote calls.
/// </summary>
public static class CorporateTemplateService
{
    public static IReadOnlyList<CorporateTemplateInfo> All { get; } =
    [
        new(CorporateTemplateId.ServiceMemo, "Служебная записка", "Краткий внутренний документ"),
        new(CorporateTemplateId.Act, "Акт", "Черновик акта для последующей проверки"),
        new(CorporateTemplateId.Letter, "Официальное письмо", "Черновик делового письма")
    ];

    private static readonly IReadOnlyDictionary<CorporateTemplateId, string> Patterns =
        new Dictionary<CorporateTemplateId, string>
        {
            [CorporateTemplateId.ServiceMemo] = """
                СЛУЖЕБНАЯ ЗАПИСКА
                Дата: {{ document_date }}
                Номер: {{ document_number }}
                Подразделение: {{ department }}
                От: {{ author }}
                Кому: {{ recipient }}
                Тема: {{ subject }}

                {{ content }}
                """,
            [CorporateTemplateId.Act] = """
                АКТ
                Дата: {{ document_date }}
                Номер: {{ document_number }}
                Подразделение: {{ department }}
                Составитель: {{ author }}
                Получатель: {{ recipient }}
                Предмет акта: {{ subject }}

                {{ content }}
                """,
            [CorporateTemplateId.Letter] = """
                ПИСЬМО
                Дата: {{ document_date }}
                Исходящий номер: {{ document_number }}
                Отправитель: {{ department }}
                Подготовил: {{ author }}
                Адресат: {{ recipient }}
                Тема: {{ subject }}

                {{ content }}
                """
        };

    public static string Preview(CorporateTemplateId kind, CorporateTemplateData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!Patterns.TryGetValue(kind, out string? pattern))
            throw new ArgumentOutOfRangeException(nameof(kind));
        string department = Required(data.Department, nameof(data.Department), 160);
        string author = Required(data.Author, nameof(data.Author), 160);
        string recipient = Required(data.Recipient, nameof(data.Recipient), 160);
        string subject = Required(data.Subject, nameof(data.Subject), 200);
        string content = Required(data.Content, nameof(data.Content), 5000, multiline: true);
        string number = Optional(data.DocumentNumber, nameof(data.DocumentNumber), 40);
        var parsed = Template.Parse(pattern);
        if (parsed.HasErrors)
            throw new InvalidOperationException("Встроенный шаблон повреждён.");

        // Strictly pass strings; never expose CLR objects/functions to untrusted template data.
        var vars = new ScriptObject
        {
            ["department"] = department,
            ["author"] = author,
            ["recipient"] = recipient,
            ["subject"] = subject,
            ["content"] = content,
            ["document_number"] = number.Length == 0 ? "б/н" : number,
            ["document_date"] = data.DocumentDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
        };
        var context = new TemplateContext();
        context.PushGlobal(vars);
        var rendered = parsed.Render(context).Replace("\r\n", "\n").TrimEnd();
        if (rendered.Length > 8000 || rendered.Count(c => c == '\n') > 100)
            throw new InvalidDataException("Слишком большой результат шаблона.");
        return rendered;
    }

    public static void CreateDocx(
        CorporateTemplateId kind, CorporateTemplateData data, string destination)
    {
        string text = Preview(kind, data);  // Validate all user data before touching any files.
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        string target = Path.GetFullPath(destination);
        if (!Path.GetExtension(target).Equals(".docx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Шаблон сохраняется только в DOCX.", nameof(destination));

        var folder = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException("Папка сохранения отсутствует или недоступна.");
        if (File.Exists(target))
            throw new IOException("Существующий документ или WORM-оригинал не перезаписывается.");

        string stage = Path.Combine(folder, ".get-template-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            using (var package = WordprocessingDocument.Create(stage, WordprocessingDocumentType.Document))
            {
                var main = package.AddMainDocumentPart();
                var body = new Body();
                string[] lines = text.Split('\n', StringSplitOptions.None);
                for (int index = 0; index < lines.Length; index++)
                {
                    string line = lines[index];
                    bool title = index == 0;
                    var paragraph = new Paragraph(
                        new ParagraphProperties(
                            new SpacingBetweenLines
                            {
                                After = title ? "240" : "120",
                                Line = "276",
                                LineRule = LineSpacingRuleValues.Auto
                            }));
                    if (title)
                        paragraph.ParagraphProperties!.Append(
                            new Justification { Val = JustificationValues.Center });
                    if (line.Length > 0)
                    {
                        var runStyle = new RunProperties(
                            new RunFonts { Ascii = "Arial", HighAnsi = "Arial", ComplexScript = "Arial" });
                        if (title) runStyle.Append(new Bold());
                        // WordprocessingML requires w:b before w:sz in run properties.
                        runStyle.Append(new FontSize { Val = title ? "26" : "22" });
                        paragraph.Append(new Run(
                            runStyle,
                            new Text(line) { Space = SpaceProcessingModeValues.Preserve }));
                    }
                    body.Append(paragraph);
                }
                // Explicit A4 settings are part of the pilot layout, not an approved GET letterhead.
                // Page dimensions/margins are in twips (1/20 pt); left margin is 30 mm.
                body.Append(new SectionProperties(
                    new PageSize { Width = 11906U, Height = 16838U },
                    new PageMargin
                    {
                        Top = 1134, Bottom = 1134,
                        Left = 1701U, Right = 1134U,
                        Header = 708U, Footer = 708U
                    }));
                main.Document = new Document(body);
                main.Document.Save();
            }
            File.Move(stage, target); // No overwrite; same-folder staging for SMB/local.
        }
        finally
        {
            if (File.Exists(stage)) File.Delete(stage);
        }
    }

    private static string Required(string? value, string field, int maxLength, bool multiline = false)
    {
        string result = Optional(value, field, maxLength, multiline);
        if (string.IsNullOrWhiteSpace(result))
            throw new ArgumentException("Обязательное поле не заполнено: " + field, field);
        return result;
    }

    private static string Optional(string? value, string field, int maxLength, bool multiline = false)
    {
        string result = (value ?? "").Replace("\r\n", "\n").Trim();
        if (result.Length > maxLength || result.Any(ch =>
                char.IsSurrogate(ch) || char.GetUnicodeCategory(ch) == UnicodeCategory.Format ||
                char.IsControl(ch) && (!multiline || ch != '\n')))
            throw new ArgumentException("Недопустимые символы или длина поля: " + field, field);
        if (!multiline && result.Contains('\n'))
            throw new ArgumentException("Поле должно состоять из одной строки: " + field, field);
        return result;
    }
}
