using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// Transport-safe, untrusted, lab-only observation. Never a signed receipt or
/// confirmation of a completed document save, PDF export or print operation.
/// </summary>
public sealed record EuroOfficeNativeReceipt(
    bool ViewLoaded,
    bool ModifiedNotificationObserved,
    bool SaveNotificationObserved,
    bool SaveCancelled,
    bool PrintNotificationObserved)
{
    public bool DocumentVerified => false;
    public bool SaveCompleted => false;
    public bool PdfReady => false;
    public bool PrintApproved => false;

    private static readonly HashSet<string> ExpectedFields = new(StringComparer.Ordinal)
    {
        "schema", "nativeEvents", "documentVerified", "saveVerified",
        "pdfVerified", "printApproved", "laboratoryOnly"
    };
    private static readonly HashSet<string> ExpectedNativeFields = new(StringComparer.Ordinal)
    {
        "viewLoaded", "modifiedNotification", "onSaveNotification",
        "onSaveCancelled", "printEndNotification"
    };

    public static EuroOfficeNativeReceipt Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 16 * 1024)
            throw new InvalidDataException("Некорректный объём отчёта нативного редактора.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = 4
            });
            var root = document.RootElement;
            ValidateObjectKeys(root, ExpectedFields);
            if (root.GetProperty("schema").GetString() != "GD31J7_EVENTS_ONLY" ||
                root.GetProperty("laboratoryOnly").ValueKind != JsonValueKind.True ||
                root.GetProperty("documentVerified").ValueKind != JsonValueKind.False ||
                root.GetProperty("saveVerified").ValueKind != JsonValueKind.False ||
                root.GetProperty("pdfVerified").ValueKind != JsonValueKind.False ||
                root.GetProperty("printApproved").ValueKind != JsonValueKind.False)
                throw new InvalidDataException("Нативный отчёт пытается подтвердить неподтверждённую операцию.");

            JsonElement native = root.GetProperty("nativeEvents");
            ValidateObjectKeys(native, ExpectedNativeFields);
            bool read(string key)
            {
                var item = native.GetProperty(key);
                if (item.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("Недопустимое поле события EuroOffice.");
                return item.GetBoolean();
            }

            return new EuroOfficeNativeReceipt(
                read("viewLoaded"), read("modifiedNotification"),
                read("onSaveNotification"), read("onSaveCancelled"),
                read("printEndNotification"));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or
                                   InvalidOperationException)
        {
            throw new InvalidDataException("Некорректная структура отчёта EuroOffice.", ex);
        }
    }

    private static void ValidateObjectKeys(JsonElement element, HashSet<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Ожидается JSON объект.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            if (!expected.Contains(property.Name) || !keys.Add(property.Name))
                throw new InvalidDataException("Незнакомое или повторное поле JSON.");
        if (keys.Count != expected.Count)
            throw new InvalidDataException("Неполный JSON отчёт.");
    }
}
