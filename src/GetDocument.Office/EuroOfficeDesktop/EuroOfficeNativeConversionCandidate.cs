using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// Observation of a bounded Editor.bin file in the isolated EuroOffice CEF
/// temporary recovery directory. This is an UNTRUSTED candidate only, not an
/// OOXML import ACK, editable document, save receipt, or corporate file access.
/// </summary>
public sealed record EuroOfficeNativeConversionCandidate(
    bool CefViewLoaded, bool EditorPathMatched,
    bool ConversionArtifactObserved, long EditorBinaryLength, string EditorBinarySha256)
{
    public bool DocumentVerified => false;
    public bool EditReady => false;
    public bool SaveCompleted => false;
    public bool PdfReady => false;
    public bool ApprovedForProduction => false;

    private const int MaximumJsonChars = 2048;
    private const long MaximumEditorBytes = 128L * 1024 * 1024;
    private static readonly HashSet<string> ExpectedFields = new(StringComparer.Ordinal)
    {
        "schema", "sessionId", "tabId", "fixtureName",
        "cefViewLoaded", "editorPathMatched", "conversionArtifactObserved",
        "editorBinaryLength", "editorBinarySha256", "documentVerified",
        "saveVerified", "pdfVerified", "printApproved", "laboratoryOnly"
    };

    public static EuroOfficeNativeConversionCandidate Parse(
        string? json, Guid sessionId, Guid tabId, string fixtureName)
    {
        if (sessionId == Guid.Empty || tabId == Guid.Empty ||
            string.IsNullOrWhiteSpace(fixtureName) ||
            json is null || json.Length is < 2 or > MaximumJsonChars)
            throw new InvalidDataException("Отсутствует безопасная квитанция EuroOffice.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = 4
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Ожидалась квитанция-объект.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!ExpectedFields.Contains(property.Name) || !seen.Add(property.Name))
                    throw new InvalidDataException("Недопустимое или дублированное поле.");
            }
            if (seen.Count != ExpectedFields.Count ||
                Text(root, "schema") != "getdoc-native-conversion-candidate-lab/1" ||
                Text(root, "sessionId") != sessionId.ToString("N") ||
                Text(root, "tabId") != tabId.ToString("N") ||
                Text(root, "fixtureName") != fixtureName ||
                Flag(root, "documentVerified") || Flag(root, "saveVerified") ||
                Flag(root, "pdfVerified") || Flag(root, "printApproved") ||
                !Flag(root, "laboratoryOnly"))
                throw new InvalidDataException("Чужая или неподтверждённая квитанция.");

            bool view = Flag(root, "cefViewLoaded");
            bool path = Flag(root, "editorPathMatched");
            bool observed = Flag(root, "conversionArtifactObserved");
            JsonElement size = root.GetProperty("editorBinaryLength");
            if (size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out long length))
                throw new InvalidDataException("Неверный размер Editor.bin.");
            string digest = Text(root, "editorBinarySha256");
            bool validHash = digest.Length == 64 &&
                digest.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
            if (observed
                ? !(view && path && length is > 0 and <= MaximumEditorBytes && validHash)
                : !(length == 0 && digest.Length == 0))
                throw new InvalidDataException("Недостоверная квитанция конвертера.");
            return new EuroOfficeNativeConversionCandidate(view, path, observed, length, digest);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Повреждённый JSON отчёт EuroOffice.", ex);
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidDataException("Неполный JSON отчёт EuroOffice.", ex);
        }
    }

    private static string Text(JsonElement item, string name)
    {
        JsonElement value = item.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Неверный тип строкового поля.");
        return value.GetString() ?? string.Empty;
    }

    private static bool Flag(JsonElement item, string name) =>
        item.GetProperty(name).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException("Неверный тип флага.")
        };
}
