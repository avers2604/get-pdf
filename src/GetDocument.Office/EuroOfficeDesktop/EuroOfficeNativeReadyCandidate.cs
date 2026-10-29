using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// A renderer-originated content-ready notification correlates with a private
/// Editor.bin conversion *candidate*, never authenticated document import.
/// Only synthetic offline CEF in a manually patched, attested SDK lab emits it.
/// </summary>
public sealed record EuroOfficeNativeReadyCandidate(
    bool CefViewLoaded, bool EditorPathMatched,
    bool EditorContentReadyNotification, bool ConversionArtifactObserved,
    long EditorBinaryLength, string EditorBinarySha256)
{
    public bool ReadyAndConversionCorrelated =>
        CefViewLoaded && EditorPathMatched &&
        EditorContentReadyNotification && ConversionArtifactObserved;

    // Do NOT change these flags based on an unauthenticated JS->CEF event.
    public bool DocumentVerified => false;
    public bool EditReady => false;
    public bool SaveCompleted => false;
    public bool PdfReady => false;
    public bool ApprovedForProduction => false;

    private static readonly HashSet<string> ExactFields = new(StringComparer.Ordinal)
    {
        "schema", "sessionId", "tabId", "fixtureName", "cefViewLoaded",
        "editorPathMatched", "editorContentReadyNotification",
        "conversionArtifactObserved", "editorBinaryLength", "editorBinarySha256",
        "documentVerified", "saveVerified", "pdfVerified", "printApproved",
        "laboratoryOnly"
    };

    public static EuroOfficeNativeReadyCandidate Parse(string? json,
        Guid expectedSession, Guid expectedTab, string expectedFixture)
    {
        if (expectedSession == Guid.Empty || expectedTab == Guid.Empty ||
            string.IsNullOrWhiteSpace(expectedFixture) ||
            json is null || json.Length is < 2 or > 2048)
            throw new InvalidDataException("Нет допустимой ready-квитанции EuroOffice.");
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = 4,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("CEF ready receipt must be JSON object.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!ExactFields.Contains(property.Name) || !names.Add(property.Name))
                    throw new InvalidDataException("Дубликат или лишнее поле ready receipt.");
            if (names.Count != ExactFields.Count ||
                Text(root, "schema") != "getdoc-native-ready-candidate-lab/1" ||
                Text(root, "sessionId") != expectedSession.ToString("N") ||
                Text(root, "tabId") != expectedTab.ToString("N") ||
                Text(root, "fixtureName") != expectedFixture ||
                Flag(root, "documentVerified") || Flag(root, "saveVerified") ||
                Flag(root, "pdfVerified") || Flag(root, "printApproved") ||
                !Flag(root, "laboratoryOnly"))
                throw new InvalidDataException("Чужой ready-сеанс либо ложная авторизация.");

            bool cef = Flag(root, "cefViewLoaded");
            bool path = Flag(root, "editorPathMatched");
            bool notified = Flag(root, "editorContentReadyNotification");
            bool converted = Flag(root, "conversionArtifactObserved");
            JsonElement sizeField = root.GetProperty("editorBinaryLength");
            if (sizeField.ValueKind != JsonValueKind.Number ||
                !sizeField.TryGetInt64(out long size))
                throw new InvalidDataException("Неверный размер конвертера.");
            string sha = Text(root, "editorBinarySha256");
            bool hashOk = sha.Length == 64 && sha.All(ch =>
                ch is >= '0' and <= '9' or >= 'a' and <= 'f');
            if (converted
                ? !(cef && path && size is > 0 and <= 8L * 1024 * 1024 && hashOk)
                : !(size == 0 && sha.Length == 0))
                throw new InvalidDataException("Недостоверный конвертер или SHA в ready receipt.");
            return new EuroOfficeNativeReadyCandidate(cef, path, notified, converted, size, sha);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Неполный или повреждённый ready JSON.", ex);
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidDataException("Отсутствуют обязательные ready-поля.", ex);
        }
    }

    private static string Text(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Неверный тип текстового ready-поля.");
        return value.GetString() ?? "";
    }

    private static bool Flag(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException("Неверный тип логического ready-поля.")
        };
}
