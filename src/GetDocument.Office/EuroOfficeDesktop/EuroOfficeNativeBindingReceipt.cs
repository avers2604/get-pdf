using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// Lab-only evidence that the pinned SDK returned the expected synthetic
/// local path after OpenLocalFile and the CEF VIEW loaded. Neither signal
/// proves OOXML parsing, editing, saved bytes or approval for production.
/// </summary>
public sealed record EuroOfficeNativeBindingReceipt(
    bool CefViewLoaded, bool EditorPathMatched, bool BindingObserved)
{
    public bool DocumentVerified => false;
    public bool SaveCompleted => false;
    public bool PrintApproved => false;
    public bool ApprovedForProduction => false;

    private const int MaximumJsonChars = 2048;
    private const string Schema = "getdoc-native-binding-lab/1";
    private static readonly HashSet<string> ExpectedFields = new(StringComparer.Ordinal)
    {
        "schema", "sessionId", "tabId", "fixtureName",
        "cefViewLoaded", "editorPathMatched", "bindingObserved",
        "documentVerified", "saveVerified", "pdfVerified",
        "printApproved", "laboratoryOnly"
    };

    public static EuroOfficeNativeBindingReceipt Parse(
        string? json, Guid expectedSession, Guid expectedTab, string expectedFixture)
    {
        if (expectedSession == Guid.Empty || expectedTab == Guid.Empty ||
            string.IsNullOrWhiteSpace(expectedFixture) ||
            json is null || json.Length is < 2 or > MaximumJsonChars)
            throw new InvalidDataException("Недействительное описание CEF binding-лаборатории.");

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Ожидался объект диагностической квитанции.");
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!ExpectedFields.Contains(property.Name) || !fields.Add(property.Name))
                    throw new InvalidDataException("Дублированное или чужое поле CEF binding.");
            }
            if (fields.Count != ExpectedFields.Count ||
                ReadString(root, "schema") != Schema ||
                ReadString(root, "sessionId") != expectedSession.ToString("N") ||
                ReadString(root, "tabId") != expectedTab.ToString("N") ||
                ReadString(root, "fixtureName") != expectedFixture ||
                ReadBool(root, "documentVerified") ||
                ReadBool(root, "saveVerified") ||
                ReadBool(root, "pdfVerified") ||
                ReadBool(root, "printApproved") ||
                !ReadBool(root, "laboratoryOnly"))
                throw new InvalidDataException("CEF binding не совпадает с лабораторным контрактом.");

            bool cef = ReadBool(root, "cefViewLoaded");
            bool path = ReadBool(root, "editorPathMatched");
            bool observed = ReadBool(root, "bindingObserved");
            if (observed && !(cef && path))
                throw new InvalidDataException("Недостоверная CEF binding-квитанция.");
            return new EuroOfficeNativeBindingReceipt(cef, path, observed);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Повреждённая CEF binding-квитанция.", ex);
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidDataException("Отсутствуют обязательные поля CEF binding.", ex);
        }
    }

    private static string ReadString(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Неверный тип строкового поля CEF binding.");
        return value.GetString() ?? string.Empty;
    }

    private static bool ReadBool(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException("Неверный тип логического поля CEF binding.")
        };
}
