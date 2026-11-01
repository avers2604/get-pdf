using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// GD-31J.27b: strict diagnostic-only view of TWO INCOMPATIBLE laboratory
/// native SaveAs protocols. No bridging, downgrade or authorization is exposed.
/// Inputs are untrusted JSON even when their fields describe native callbacks.
/// This parser is not a replacement for independent OOXML validation.
/// </summary>
public sealed class EuroOfficeSaveAsProtocolDiagnostic
{
    private EuroOfficeSaveAsProtocolDiagnostic(
        string sourceProtocol, string diagnosticState,
        string reasonCode, string outputSha256, bool nativeOrder)
    {
        SourceProtocol = sourceProtocol;
        DiagnosticState = diagnosticState;
        ReasonCode = reasonCode;
        OutputSha256 = outputSha256;
        NativeEventOrderSelfReported = nativeOrder;
    }

    public string SourceProtocol { get; }
    public string DiagnosticState { get; }
    public string ReasonCode { get; }
    public string OutputSha256 { get; }
    public bool NativeEventOrderSelfReported { get; }

    // Even a correctly formatted native v2 event order is *self-reported*.
    public bool NativeSaveProven => false;
    public bool DocumentVerified => false;
    public bool SaveVerified => false;
    public bool ApprovedForProduction => false;

    private static readonly HashSet<string> AllowedFixtures = new(StringComparer.Ordinal)
    {
        "gd31j6_writer.docx", "gd31j6_calc.xlsx", "gd31j6_impress.pptx"
    };

    private static readonly HashSet<string> ExactFields = new(StringComparer.Ordinal)
    {
        "schema", "sourceProtocol", "sessionId", "tabId", "fixtureName",
        "diagnosticState", "reasonCode", "outputSha256",
        "nativeEventOrderSelfReported", "nativeSaveProven", "documentVerified",
        "saveVerified", "approvedForProduction", "laboratoryOnly"
    };

    private static readonly HashSet<string> GuardedFailures = new(StringComparer.Ordinal)
    {
        "not_started", "dialog_missing", "invalid_dialog",
        "duplicate_dialog", "target_conflict", "cancelled",
        "timeout", "target_not_assigned", "save_not_acknowledged",
        "source_modified", "private_output_invalid", "candidate_missing"
    };

    public static EuroOfficeSaveAsProtocolDiagnostic Parse(
        string? json, Guid expectedSession, Guid expectedTab,
        string expectedFixture)
    {
        if (json is null || json.Length is < 2 or > 2048 ||
            expectedSession == Guid.Empty || expectedTab == Guid.Empty ||
            expectedSession == expectedTab || !AllowedFixtures.Contains(expectedFixture))
            throw new InvalidDataException("Unapproved synthetic SaveAs protocol diagnostic.");

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = 3,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            JsonElement obj = doc.RootElement;
            if (obj.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Expected a synthetic SaveAs diagnostic object.");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in obj.EnumerateObject())
                if (!ExactFields.Contains(property.Name) || !seen.Add(property.Name))
                    throw new InvalidDataException("Unknown or duplicate SaveAs diagnostic key.");

            if (seen.Count != ExactFields.Count ||
                Text(obj, "schema") != "getdoc-native-saveas-protocol-diagnostic-lab/1" ||
                Text(obj, "sessionId") != expectedSession.ToString("N") ||
                Text(obj, "tabId") != expectedTab.ToString("N") ||
                Text(obj, "fixtureName") != expectedFixture ||
                Flag(obj, "nativeSaveProven") ||
                Flag(obj, "documentVerified") ||
                Flag(obj, "saveVerified") ||
                Flag(obj, "approvedForProduction") ||
                !Flag(obj, "laboratoryOnly"))
                throw new InvalidDataException("Foreign, forged or privileged SaveAs diagnostic.");

            string lineage = Text(obj, "sourceProtocol");
            string state = Text(obj, "diagnosticState");
            string reason = Text(obj, "reasonCode");
            string sha = Text(obj, "outputSha256");
            bool ordered = Flag(obj, "nativeEventOrderSelfReported");

            bool guarded = lineage == "gd31j28-guarded/2";
            if (lineage != "gd31j26-pinned/1" && !guarded)
                throw new InvalidDataException("Unknown synthetic SaveAs lineage.");

            bool candidate = state == "synthetic_candidate_untrusted";
            if (!candidate && state != "unconfirmed")
                throw new InvalidDataException("Unknown synthetic SaveAs diagnostic state.");

            if (candidate
                ? (reason != "candidate_untrusted" || !ValidSha(sha) ||
                   ordered != guarded)
                : (sha.Length != 0 || ordered ||
                   (guarded
                       ? !GuardedFailures.Contains(reason)
                       : reason != "unspecified_failure")))
                throw new InvalidDataException("Contradictory SaveAs diagnostic state/lineage.");

            return new EuroOfficeSaveAsProtocolDiagnostic(
                lineage, state, reason, sha, ordered);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Malformed SaveAs protocol diagnostic JSON.", ex);
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidDataException("Missing SaveAs protocol diagnostic field.", ex);
        }
    }

    private static bool ValidSha(string text) =>
        text.Length == 64 &&
        text.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Text(JsonElement obj, string field)
    {
        JsonElement value = obj.GetProperty(field);
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("SaveAs diagnostic text field has invalid type.");
        return value.GetString() ?? "";
    }

    private static bool Flag(JsonElement obj, string field) =>
        obj.GetProperty(field).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException("SaveAs diagnostic flag has invalid type.")
        };
}
