using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// Untrusted CEF native Save As dialog receipt for a preselected, private
/// disposable OOXML target. Even positive callbacks do NOT authorize WORM,
/// corporate document save, publication or fidelity claims.
/// </summary>
public sealed record EuroOfficeNativeSaveAsReceipt(
    Guid SessionId, Guid TabId, string FixtureName, string SelectedFormat, string SourceBeforeSha256, string SourceAfterSha256,
    bool SaveAsRequestSeen, bool SaveAsTargetAccepted,
    bool SaveNotification, bool SaveCancelled,
    bool CandidateWritten, string CandidateName,
    long CandidateLength, string CandidateSha256)
{
    public bool SaveCompleted => false;
    public bool DocumentVerified => false;
    public bool AuthorizedForPublication => false;

    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "schema", "sessionId", "tabId", "fixtureName", "selectedFormat",
        "sourceBeforeSha256", "sourceAfterSha256",
        "saveAsRequestSeen", "saveAsTargetAccepted", "saveNotification",
        "saveCancelled", "candidateWritten", "candidateName",
        "candidateLength", "candidateSha256", "documentVerified",
        "saveVerified", "pdfVerified", "printApproved", "laboratoryOnly"
    };

    public static string ValidateFormat(string fixtureName, string selectedFormat)
    {
        string required = fixtureName switch
        {
            "gd31j6_writer.docx" => "docx",
            "gd31j6_calc.xlsx" => "xlsx",
            "gd31j6_impress.pptx" => "pptx",
            _ => throw new ArgumentException("Только разрешённые синтетические файлы.")
        };
        if (!string.Equals(required, selectedFormat, StringComparison.Ordinal))
            throw new ArgumentException("Конвертация в другой формат не разрешена до лаборатории.");
        return required;
    }

    public static string CandidateNameFor(Guid sessionId, string format)
    {
        if (sessionId == Guid.Empty || format is not ("docx" or "xlsx" or "pptx"))
            throw new ArgumentException("Недопустимое имя кандидата.");
        return "gd31j26-saveas-candidate-" + sessionId.ToString("N") + "." + format;
    }

    public static EuroOfficeNativeSaveAsReceipt Parse(string? raw, Guid sessionId,
        Guid tabId, string fixtureName, string selectedFormat)
    {
        if (sessionId == Guid.Empty || tabId == Guid.Empty ||
            raw is null || raw.Length is < 2 or > 2048)
            throw new InvalidDataException("Недопустимый CEF Save As JSON.");
        string allowedFormat, candidate;
        try
        {
            allowedFormat = ValidateFormat(fixtureName, selectedFormat);
            candidate = CandidateNameFor(sessionId, allowedFormat);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("Недопустимый Save As запрос.", ex);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(raw, new JsonDocumentOptions
            {
                MaxDepth = 4,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            JsonElement json = document.RootElement;
            if (json.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Ожидалась JSON-квитанция объекта.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty item in json.EnumerateObject())
                if (!Fields.Contains(item.Name) || !seen.Add(item.Name))
                    throw new InvalidDataException("Чужое или повторное поле Save As.");
            if (seen.Count != Fields.Count ||
                Text(json, "schema") != "getdoc-native-saveas-private-lab/1" ||
                Text(json, "sessionId") != sessionId.ToString("N") ||
                Text(json, "tabId") != tabId.ToString("N") ||
                Text(json, "fixtureName") != fixtureName ||
                Text(json, "selectedFormat") != allowedFormat ||
                Flag(json, "documentVerified") || Flag(json, "saveVerified") ||
                Flag(json, "pdfVerified") || Flag(json, "printApproved") ||
                !Flag(json, "laboratoryOnly"))
                throw new InvalidDataException("Чужая сессия или ложные разрешения.");
            string first = Text(json, "sourceBeforeSha256");
            string last = Text(json, "sourceAfterSha256");
            if (!ValidHash(first) || last != first)
                throw new InvalidDataException("Эталон изменился в ходе Save As.");

            bool dialog = Flag(json, "saveAsRequestSeen");
            bool target = Flag(json, "saveAsTargetAccepted");
            bool saved = Flag(json, "saveNotification");
            bool cancelled = Flag(json, "saveCancelled");
            bool written = Flag(json, "candidateWritten");
            string name = Text(json, "candidateName");
            string hash = Text(json, "candidateSha256");
            var number = json.GetProperty("candidateLength");
            if (number.ValueKind != JsonValueKind.Number ||
                !number.TryGetInt64(out long length))
                throw new InvalidDataException("Неверный размер Save As.");
            if (target && !dialog)
                throw new InvalidDataException("Путь сохранения без диалога запрещён.");
            if (written
                ? !(dialog && target && saved && !cancelled &&
                    name == candidate && length is > 0 and <= 128L * 1024 * 1024 &&
                    ValidHash(hash) && hash != first)
                : !(name.Length == 0 && length == 0 && hash.Length == 0))
                throw new InvalidDataException("Не подтверждён результат сохранения.");
            return new EuroOfficeNativeSaveAsReceipt(
                sessionId, tabId, fixtureName, allowedFormat,
                first, last, dialog, target,
                saved, cancelled, written, name, length, hash);
        }
        catch (JsonException ex) { throw new InvalidDataException("Повреждённый JSON.", ex); }
        catch (KeyNotFoundException ex) { throw new InvalidDataException("Неполный JSON.", ex); }
    }

    private static bool ValidHash(string value) =>
        value.Length == 64 && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Text(JsonElement json, string name)
    {
        var value = json.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Неверный тип текстового поля.");
        return value.GetString() ?? "";
    }

    private static bool Flag(JsonElement json, string name) =>
        json.GetProperty(name).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException("Неверный тип флага.")
        };
}
