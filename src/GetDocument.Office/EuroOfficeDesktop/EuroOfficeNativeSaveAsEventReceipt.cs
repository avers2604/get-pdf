using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// GD-31J.28: a strict, synthetic-only report of OBSERVED CEF callbacks and
/// the local Qt host's terminal classification. Codes are diagnostic, NOT
/// attested statements about OOXML fidelity, durable save or provenance.
/// The nested /1 receipt stays supported for old tests, but native RunAsync
/// requires this exact /2 envelope and never silently falls back to /1.
/// </summary>
public sealed record EuroOfficeNativeSaveAsEventReceipt(
    EuroOfficeNativeSaveAsReceipt Core, string FailureCode, string Phase,
    int DialogCount, int OnSaveCount, int DialogSequence, int OnSaveSequence)
{
    private static readonly HashSet<string> ExactFields = new(StringComparer.Ordinal)
    {
        "schema", "sessionId", "tabId", "fixtureName", "failureCode",
        "phase", "dialogCount", "onSaveCount", "dialogSequence",
        "onSaveSequence", "receipt"
    };

    private static readonly HashSet<string> AllowedCodes = new(StringComparer.Ordinal)
    {
        "not_started", "dialog_missing", "invalid_dialog",
        "duplicate_dialog", "target_conflict", "cancelled",
        "timeout", "target_not_assigned", "save_not_acknowledged",
        "source_modified", "private_output_invalid", "candidate_missing",
        "candidate_untrusted"
    };

    private static readonly HashSet<string> AllowedPhases = new(StringComparer.Ordinal)
    {
        "not_started", "request_sent", "dialog_observed",
        "target_assigned", "on_save_observed", "quarantined"
    };

    public bool CorrelatedNativeCallbacks =>
        DialogCount == 1 && OnSaveCount == 1 &&
        DialogSequence > 0 && OnSaveSequence > DialogSequence &&
        Core.SaveAsRequestSeen && Core.SaveAsTargetAccepted &&
        Core.SaveNotification && !Core.SaveCancelled;

    public bool SaveCompleted => false;
    public bool AuthorizedForPublication => false;
    public bool DocumentVerified => false;

    public static EuroOfficeNativeSaveAsEventReceipt Parse(string? raw,
        Guid expectedSession, Guid expectedTab,
        string expectedFixture, string expectedFormat)
    {
        if (raw is null || raw.Length is < 2 or > 4096 ||
            expectedSession == Guid.Empty || expectedTab == Guid.Empty)
            throw new InvalidDataException("Недопустимый размер/сеанс CEF event receipt.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw, new JsonDocumentOptions
            {
                MaxDepth = 5, AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            JsonElement obj = document.RootElement;
            if (obj.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("CEF event receipt не объект.");
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in obj.EnumerateObject())
                if (!ExactFields.Contains(property.Name) || !fields.Add(property.Name))
                    throw new InvalidDataException("Чужое/повторное поле CEF receipt.");
            if (fields.Count != ExactFields.Count ||
                GetString(obj, "schema") != "getdoc-native-saveas-event-lab/2" ||
                GetString(obj, "sessionId") != expectedSession.ToString("N") ||
                GetString(obj, "tabId") != expectedTab.ToString("N") ||
                GetString(obj, "fixtureName") != expectedFixture)
                throw new InvalidDataException("Версия/сессия CEF receipt не соответствует запросу.");

            string code = GetString(obj, "failureCode");
            string phase = GetString(obj, "phase");
            if (!AllowedCodes.Contains(code) || !AllowedPhases.Contains(phase))
                throw new InvalidDataException("Незнакомый код отказа или этап.");
            int dialogCount = GetCount(obj, "dialogCount", 64);
            int onSaveCount = GetCount(obj, "onSaveCount", 64);
            int dialogSequence = GetCount(obj, "dialogSequence", 128);
            int onSaveSequence = GetCount(obj, "onSaveSequence", 128);
            if ((dialogCount == 0) != (dialogSequence == 0) ||
                (onSaveCount == 0) != (onSaveSequence == 0) ||
                (dialogSequence > 0 && dialogSequence > dialogCount + onSaveCount) ||
                (onSaveSequence > 0 && onSaveSequence > dialogCount + onSaveCount) ||
                (dialogSequence > 0 && onSaveSequence > 0 &&
                 dialogSequence == onSaveSequence))
                throw new InvalidDataException("Невозможный порядок CEF-событий.");

            JsonElement nested = obj.GetProperty("receipt");
            if (nested.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Отсутствует базовая квитанция CEF.");
            EuroOfficeNativeSaveAsReceipt core =
                EuroOfficeNativeSaveAsReceipt.Parse(nested.GetRawText(),
                    expectedSession, expectedTab, expectedFixture, expectedFormat);
            if (core.SaveAsRequestSeen && dialogCount == 0 ||
                (onSaveCount > 0) != core.SaveNotification ||
                (core.CandidateWritten && phase != "quarantined") ||
                (!core.CandidateWritten && phase == "quarantined") ||
                (!core.CandidateWritten && onSaveCount > 0 &&
                 phase != "on_save_observed") ||
                (!core.CandidateWritten && onSaveCount == 0 &&
                 core.SaveAsTargetAccepted && phase != "target_assigned") ||
                (!core.CandidateWritten && onSaveCount == 0 &&
                 !core.SaveAsTargetAccepted && dialogCount > 0 &&
                 phase != "dialog_observed") ||
                core.CandidateWritten && (
                    code != "candidate_untrusted" ||
                    phase != "quarantined" ||
                    dialogCount != 1 || onSaveCount != 1 ||
                    !core.SaveAsTargetAccepted || core.SaveCancelled ||
                    onSaveSequence <= dialogSequence) ||
                code == "candidate_untrusted" && !core.CandidateWritten ||
                code == "duplicate_dialog" && dialogCount < 2 ||
                code == "dialog_missing" && dialogCount != 0 ||
                code == "cancelled" && !core.SaveCancelled ||
                code == "save_not_acknowledged" && onSaveCount != 0 ||
                code == "timeout" && onSaveCount != 0)
                throw new InvalidDataException("Противоречивый native failureCode.");

            return new EuroOfficeNativeSaveAsEventReceipt(
                core, code, phase, dialogCount, onSaveCount,
                dialogSequence, onSaveSequence);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Неполный/повреждённый CEF event JSON.", ex);
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidDataException("Отсутствует обязательное поле CEF event JSON.", ex);
        }
    }

    private static string GetString(JsonElement obj, string name)
    {
        JsonElement item = obj.GetProperty(name);
        if (item.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Текстовое поле CEF event receipt неверного типа.");
        return item.GetString() ?? "";
    }

    private static int GetCount(JsonElement obj, string name, int max)
    {
        JsonElement item = obj.GetProperty(name);
        if (item.ValueKind != JsonValueKind.Number ||
            !item.TryGetInt32(out int value) || value < 0 || value > max)
            throw new InvalidDataException("Неверный ограниченный счётчик CEF events.");
        return value;
    }
}
