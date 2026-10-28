using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// Untrusted, bounded JSONL messages from a future isolated EuroOffice host.
/// GD-31J.8 only accepts observations, not open/save/print commands.
/// No real editor host speaks this protocol yet; the previous CEF lab prints
/// a separate diagnostic receipt when its process exits.
/// </summary>
public sealed record EuroOfficeNativeIpcFrame(
    string Kind, string SessionId, long Sequence, string? EventName, int? EventCode,
    EuroOfficeNativeReceipt? Receipt);

public static class EuroOfficeNativeIpc
{
    public const string Protocol = "getdoc-eurodesktop-ipc/1";
    private const int MaxFrameChars = 8192;
    private static readonly IReadOnlyDictionary<string, int> AllowedEvents =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["view-loaded"] = 10,
            ["modified"] = 1012,
            ["on-save"] = 3002,
            ["print-end"] = 5013,
            ["closed"] = 2001
        };

    public static EuroOfficeNativeIpcFrame Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxFrameChars ||
            json.Contains('\n') || json.Contains('\r'))
            throw new InvalidDataException("Недопустимая длина или формат IPC-сообщения.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = 8,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("IPC-сообщение должно быть объектом.");

            string kind = StrictString(root, "kind");
            var expected = kind switch
            {
                "event" => new[] { "protocol", "kind", "sessionId", "sequence", "event", "eventCode" },
                "snapshot" => new[] { "protocol", "kind", "sessionId", "sequence", "receipt" },
                _ => throw new InvalidDataException("Неизвестный тип сообщения нативного хоста.")
            };
            ValidateKeys(root, expected);
            if (StrictString(root, "protocol") != Protocol)
                throw new InvalidDataException("Неподдерживаемый протокол нативного редактора.");
            string session = StrictString(root, "sessionId");
            if (!Guid.TryParseExact(session, "D", out var guid) ||
                guid == Guid.Empty || guid.ToString("D") != session)
                throw new InvalidDataException("Неверный идентификатор IPC-сеанса.");

            var seq = root.GetProperty("sequence");
            if (seq.ValueKind != JsonValueKind.Number || !seq.TryGetInt64(out long sequence) ||
                sequence <= 0 || sequence > 1_000_000)
                throw new InvalidDataException("Недопустимый порядковый номер IPC.");

            if (kind == "event")
            {
                string eventName = StrictString(root, "event");
                JsonElement code = root.GetProperty("eventCode");
                if (code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out int id) ||
                    !AllowedEvents.TryGetValue(eventName, out int expectedCode) || id != expectedCode)
                    throw new InvalidDataException("Неизвестное событие EuroOffice.");
                return new EuroOfficeNativeIpcFrame(kind, session, sequence, eventName, id, null);
            }

            var receipt = root.GetProperty("receipt");
            if (receipt.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Отсутствует отчёт нативного процесса.");
            return new EuroOfficeNativeIpcFrame(kind, session, sequence, null, null,
                EuroOfficeNativeReceipt.Parse(receipt.GetRawText()));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or
                                    FormatException or InvalidOperationException)
        {
            throw new InvalidDataException("Повреждённое IPC-сообщение EuroOffice.", ex);
        }
    }

    public static async Task<EuroOfficeNativeIpcFrame> ReadFrameAsync(
        TextReader reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        cancellationToken.ThrowIfCancellationRequested();
        char[] single = new char[1];
        var frame = new System.Text.StringBuilder(256);
        for (int i = 0; i < MaxFrameChars; i++)
        {
            int got = await reader.ReadAsync(single.AsMemory(), cancellationToken);
            if (got == 0)
            {
                if (frame.Length == 0)
                    throw new EndOfStreamException("Нативный хост завершил поток.");
                throw new InvalidDataException("Хост передал незавершённое сообщение.");
            }
            if (single[0] == '\n')
                return Parse(frame.ToString());
            if (single[0] == '\r' || single[0] == '\0')
                throw new InvalidDataException("Неожиданный символ IPC.");
            frame.Append(single[0]);
        }
        throw new InvalidDataException("Превышен лимит IPC-сообщения.");
    }

    private static string StrictString(JsonElement root, string name)
    {
        var item = root.GetProperty(name);
        if (item.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Поле IPC должно быть строкой.");
        return item.GetString() ?? throw new InvalidDataException("Пустое IPC-поле.");
    }

    private static void ValidateKeys(JsonElement element, IEnumerable<string> required)
    {
        var allowed = new HashSet<string>(required, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
                throw new InvalidDataException("Неизвестное или повторное поле IPC.");
        if (seen.Count != allowed.Count)
            throw new InvalidDataException("Недостаточно полей IPC.");
    }
}
