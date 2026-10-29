using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// One untrusted report from the pinned SDK's synthetic-only private working
/// copy. Callback + bytes do not prove lossless OOXML round-trip, provenance,
/// ready state or that a WORM/customer file can be safely saved.
/// </summary>
public sealed record EuroOfficeNativeRoundTripReceipt(
    string SourceBeforeSha256, string SourceAfterSha256,
    bool ModifiedNotification, bool SaveNotification, bool SaveCancelled,
    bool CandidateWritten, string CandidateName, long CandidateLength,
    string CandidateSha256)
{
    public bool DocumentVerified => false;
    public bool SaveCompleted => false;
    public bool AuthorizedForPublication => false;

    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "schema", "sessionId", "tabId", "fixtureName",
        "sourceBeforeSha256", "sourceAfterSha256",
        "modifiedNotification", "saveNotification", "saveCancelled",
        "candidateWritten", "candidateName", "candidateLength", "candidateSha256",
        "documentVerified", "saveVerified", "pdfVerified", "printApproved",
        "laboratoryOnly"
    };

    public static string ExpectedCandidateName(Guid sessionId, string fixtureName)
    {
        if (sessionId == Guid.Empty ||
            fixtureName is not ("gd31j6_writer.docx" or
                "gd31j6_calc.xlsx" or "gd31j6_impress.pptx"))
            throw new ArgumentException("Нужны sessionId и синтетический OOXML fixture.");
        return "gd31j25-native-candidate-" + sessionId.ToString("N") +
            Path.GetExtension(fixtureName);
    }

    public static EuroOfficeNativeRoundTripReceipt Parse(string? raw,
        Guid sessionId, Guid tabId, string fixtureName)
    {
        if (sessionId == Guid.Empty || tabId == Guid.Empty ||
            raw is null || raw.Length is < 2 or > 2048)
            throw new InvalidDataException("Неверный native roundtrip receipt.");
        string expectedName;
        try { expectedName = ExpectedCandidateName(sessionId, fixtureName); }
        catch (ArgumentException ex) { throw new InvalidDataException("Чужой fixture.", ex); }

        try
        {
            using JsonDocument document = JsonDocument.Parse(raw, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
            JsonElement json = document.RootElement;
            if (json.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Ожидался JSON объект.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty item in json.EnumerateObject())
                if (!Fields.Contains(item.Name) || !seen.Add(item.Name))
                    throw new InvalidDataException("Чужое или дублированное поле.");
            if (seen.Count != Fields.Count ||
                Text(json, "schema") != "getdoc-synthetic-roundtrip-lab/1" ||
                Text(json, "sessionId") != sessionId.ToString("N") ||
                Text(json, "tabId") != tabId.ToString("N") ||
                Text(json, "fixtureName") != fixtureName ||
                Flag(json, "documentVerified") || Flag(json, "saveVerified") ||
                Flag(json, "pdfVerified") || Flag(json, "printApproved") ||
                !Flag(json, "laboratoryOnly"))
                throw new InvalidDataException("Чужая сессия или ложное подтверждение сохранения.");

            string oldHash = Text(json, "sourceBeforeSha256");
            string afterHash = Text(json, "sourceAfterSha256");
            if (!ValidHash(oldHash) || oldHash != afterHash)
                throw new InvalidDataException("Исходный fixture изменился или контрольная сумма неправильная.");
            bool modified = Flag(json, "modifiedNotification");
            bool notified = Flag(json, "saveNotification");
            bool cancelled = Flag(json, "saveCancelled");
            bool created = Flag(json, "candidateWritten");
            string candidateName = Text(json, "candidateName");
            string candidateSha = Text(json, "candidateSha256");
            JsonElement sizeValue = json.GetProperty("candidateLength");
            if (sizeValue.ValueKind != JsonValueKind.Number ||
                !sizeValue.TryGetInt64(out long length))
                throw new InvalidDataException("Неверный размер candidate.");

            if (created
                ? !(modified && notified && !cancelled &&
                    candidateName == expectedName &&
                    length is > 0 and <= 128L * 1024 * 1024 &&
                    ValidHash(candidateSha) && candidateSha != oldHash)
                : !(candidateName.Length == 0 && length == 0 && candidateSha.Length == 0))
                throw new InvalidDataException("Native candidate не согласован с событиями/размером.");
            return new EuroOfficeNativeRoundTripReceipt(
                oldHash, afterHash, modified, notified, cancelled,
                created, candidateName, length, candidateSha);
        }
        catch (JsonException ex) { throw new InvalidDataException("Повреждённый JSON.", ex); }
        catch (KeyNotFoundException ex) { throw new InvalidDataException("Неполный JSON.", ex); }
    }

    private static bool ValidHash(string value) =>
        value.Length == 64 && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Text(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Неверный тип строкового поля.");
        return value.GetString() ?? string.Empty;
    }

    private static bool Flag(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException("Неверный тип флага.")
        };
}
