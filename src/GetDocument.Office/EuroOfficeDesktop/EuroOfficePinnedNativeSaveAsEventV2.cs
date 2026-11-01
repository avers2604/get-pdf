using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// GD-31J.27c optional, pinned-SDK v2 event receipt. Exact native sender
/// counts and ordering are self-reported, NOT authenticated OS/process proof.
/// v1 is nested for diagnostic continuity, not automatic downgrade. This type
/// never authorizes real documents, WORM, printing or production SaveAs.
/// </summary>
public sealed class EuroOfficePinnedNativeSaveAsEventV2
{
    private EuroOfficePinnedNativeSaveAsEventV2(
        string code, string phase, string outputSha256, bool ordered)
    {
        FailureCode = code;
        Phase = phase;
        OutputSha256 = outputSha256;
        NativeCallbackSequenceSelfReported = ordered;
    }

    public string FailureCode { get; }
    public string Phase { get; }
    public string OutputSha256 { get; }
    public bool NativeCallbackSequenceSelfReported { get; }
    public bool NativeSaveProven => false;
    public bool DocumentVerified => false;
    public bool SaveVerified => false;
    public bool ApprovedForProduction => false;

    private static readonly HashSet<string> Fixtures = new(StringComparer.Ordinal)
    {
        "gd31j6_writer.docx", "gd31j6_calc.xlsx", "gd31j6_impress.pptx"
    };
    private static readonly HashSet<string> OuterFields = new(StringComparer.Ordinal)
    {
        "schema", "sessionId", "tabId", "fixtureName", "failureCode", "phase",
        "dialogCount", "onSaveCount", "dialogSequence", "onSaveSequence",
        "invalidDialogObserved", "unexpectedOnSaveObserved", "receipt"
    };
    private static readonly HashSet<string> InnerFields = new(StringComparer.Ordinal)
    {
        "schema", "sessionId", "tabId", "fixtureName",
        "nativeSaveRequestObserved", "savePathProvided", "nativeOnSaveAfterAnswer",
        "saveCancelledAfterAnswer", "sourceUnchanged", "publishedToDisposableOutput",
        "outputSha256", "nativeSaveProven", "documentVerified", "saveVerified",
        "pdfVerified", "printApproved", "approvedForProduction", "laboratoryOnly"
    };
    private static readonly HashSet<string> Phases = new(StringComparer.Ordinal)
    {
        "not_started", "dialog_observed", "target_assigned",
        "on_save_observed", "published_disposable"
    };
    private static readonly HashSet<string> FailureCodes = new(StringComparer.Ordinal)
    {
        "candidate_untrusted", "invalid_dialog", "duplicate_dialog",
        "unexpected_on_save", "duplicate_on_save", "cancelled", "source_modified",
        "dialog_missing", "target_not_assigned", "timeout",
        "save_not_acknowledged", "candidate_missing"
    };

    public static EuroOfficePinnedNativeSaveAsEventV2 Parse(
        string? json, Guid session, Guid tab, string fixture)
    {
        if (json is null || json.Length is < 2 or > 4096 ||
            session == Guid.Empty || tab == Guid.Empty || session == tab ||
            !Fixtures.Contains(fixture))
            throw new InvalidDataException("Not a pinned synthetic v2 event receipt.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = 6,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            JsonElement root = document.RootElement;
            Exact(root, OuterFields);
            if (Text(root, "schema") != "getdoc-native-saveas-pinned-event-lab/2" ||
                Text(root, "sessionId") != session.ToString("N") ||
                Text(root, "tabId") != tab.ToString("N") ||
                Text(root, "fixtureName") != fixture)
                throw new InvalidDataException("Pinned v2 session/fixture/schema mismatch.");

            string code = Text(root, "failureCode");
            string phase = Text(root, "phase");
            if (!FailureCodes.Contains(code) || !Phases.Contains(phase))
                throw new InvalidDataException("Pinned v2 unknown reason or phase.");
            int dialogs = Count(root, "dialogCount", 64);
            int saves = Count(root, "onSaveCount", 64);
            int ds = Count(root, "dialogSequence", 128);
            int ns = Count(root, "onSaveSequence", 128);
            bool invalid = Flag(root, "invalidDialogObserved");
            bool unexpected = Flag(root, "unexpectedOnSaveObserved");
            if ((dialogs == 0) != (ds == 0) ||
                (saves == 0) != (ns == 0) ||
                ds > dialogs + saves || ns > dialogs + saves ||
                (ds != 0 && ns != 0 && ds == ns) ||
                (invalid && dialogs == 0) || (unexpected && saves == 0))
                throw new InvalidDataException("Impossible pinned v2 callback sequence.");

            JsonElement nested = root.GetProperty("receipt");
            Exact(nested, InnerFields);
            if (Text(nested, "schema") != "getdoc-native-saveas-candidate-lab/1" ||
                Text(nested, "sessionId") != session.ToString("N") ||
                Text(nested, "tabId") != tab.ToString("N") ||
                Text(nested, "fixtureName") != fixture ||
                Flag(nested, "nativeSaveProven") || Flag(nested, "documentVerified") ||
                Flag(nested, "saveVerified") || Flag(nested, "pdfVerified") ||
                Flag(nested, "printApproved") || Flag(nested, "approvedForProduction") ||
                !Flag(nested, "laboratoryOnly"))
                throw new InvalidDataException("Pinned v2 nested v1 privilege or scope violation.");

            bool request = Flag(nested, "nativeSaveRequestObserved");
            bool path = Flag(nested, "savePathProvided");
            bool after = Flag(nested, "nativeOnSaveAfterAnswer");
            bool cancelled = Flag(nested, "saveCancelledAfterAnswer");
            bool unchanged = Flag(nested, "sourceUnchanged");
            bool published = Flag(nested, "publishedToDisposableOutput");
            string sha = Text(nested, "outputSha256");
            if ((request && dialogs == 0) ||
                (dialogs == 1 && !invalid && !request) ||
                (path && dialogs == 0) ||
                (after && (saves == 0 || !path)) ||
                (cancelled && (saves == 0 || !path)) ||
                (after && cancelled) ||
                (published && (!request || !path || !after || cancelled || !unchanged)) ||
                (published ? !ValidSha(sha) : sha.Length != 0))
                throw new InvalidDataException("Pinned v2 nested candidate telemetry conflicts.");

            string expectedPhase = published ? "published_disposable" :
                saves > 0 ? "on_save_observed" :
                path ? "target_assigned" :
                dialogs > 0 ? "dialog_observed" : "not_started";
            if (phase != expectedPhase)
                throw new InvalidDataException("Pinned v2 observed phase mismatch.");

            string expectedCode = invalid ? "invalid_dialog" :
                dialogs > 1 ? "duplicate_dialog" :
                unexpected ? "unexpected_on_save" :
                saves > 1 ? "duplicate_on_save" :
                cancelled ? "cancelled" :
                !unchanged ? "source_modified" :
                dialogs == 0 ? "dialog_missing" :
                !path ? "target_not_assigned" :
                !after ? "save_not_acknowledged" :
                !published ? "candidate_missing" : "candidate_untrusted";
            if (code != expectedCode &&
                !((expectedCode == "save_not_acknowledged" ||
                   expectedCode == "candidate_missing") &&
                  !published && code == "timeout"))
                throw new InvalidDataException("Pinned v2 reported reason contradicts events.");

            if (published)
            {
                if (dialogs != 1 || saves != 1 || ds != 1 || ns != 2 ||
                    invalid || unexpected || !after || cancelled)
                    throw new InvalidDataException("Pinned v2 does not have a unique ordered SaveAs.");

                // Existing v1 parser remains authoritative only for positive
                // output; negative v1 may be contradictory after a late duplicate.
                _ = EuroOfficeNativeSaveAsCandidate.Parse(
                    nested.GetRawText(), session, tab, fixture);
            }
            return new EuroOfficePinnedNativeSaveAsEventV2(
                code, phase, published ? sha : "", published);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Malformed pinned v2 native JSON.", ex);
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidDataException("Missing pinned v2 field.", ex);
        }
    }

    private static void Exact(JsonElement value, HashSet<string> fields)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Pinned v2 expected an object.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty field in value.EnumerateObject())
            if (!fields.Contains(field.Name) || !found.Add(field.Name))
                throw new InvalidDataException("Unknown or duplicate pinned v2 JSON field.");
        if (found.Count != fields.Count)
            throw new InvalidDataException("Missing pinned v2 JSON field.");
    }

    private static string Text(JsonElement value, string field)
    {
        JsonElement item = value.GetProperty(field);
        if (item.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Pinned v2 text field has wrong type.");
        return item.GetString() ?? "";
    }

    private static bool Flag(JsonElement value, string field) =>
        value.GetProperty(field).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException("Pinned v2 flag has wrong type.")
        };

    private static int Count(JsonElement value, string field, int limit)
    {
        JsonElement item = value.GetProperty(field);
        if (item.ValueKind != JsonValueKind.Number ||
            !item.TryGetInt32(out int number) || number < 0 || number > limit)
            throw new InvalidDataException("Pinned v2 count outside bounds.");
        return number;
    }

    private static bool ValidSha(string sha) =>
        sha.Length == 64 && sha.All(c =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
