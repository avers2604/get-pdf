using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// GD-31J.27a LAB-ONLY diagnostic. Records a separately pinned native process
/// launch and an independently correlated OOXML candidate. JSON is not a
/// signature, and pinned-path checks are not process/OS attestation.
/// Never use this type to authorize editing, WORM writes, printing or release.
/// </summary>
public sealed class EuroOfficeSupervisedNativeOoxmlCandidate
{
    private EuroOfficeSupervisedNativeOoxmlCandidate(
        string nativeBinarySha256, EuroOfficeNativeOoxmlJoinCandidate joined)
    {
        NativeBinarySha256 = nativeBinarySha256;
        Joined = joined;
    }

    public string NativeBinarySha256 { get; }
    public EuroOfficeNativeOoxmlJoinCandidate Joined { get; }
    public bool NativeProcessExitZero => true;
    public bool NativeBinaryPinMatched => true;
    public bool StdoutSingleLineBounded => true;
    public bool NativeSaveProven => false;
    public bool DocumentVerified => false;
    public bool SaveVerified => false;
    public bool ApprovedForProduction => false;

    private static readonly HashSet<string> ExactFields = new(StringComparer.Ordinal)
    {
        "schema", "sessionId", "tabId", "fixtureName",
        "nativeBinarySha256", "nativeProcessExitZero", "nativeBinaryPinMatched",
        "stdoutSingleLineBounded", "joinedCandidate", "nativeSaveProven",
        "documentVerified", "saveVerified", "approvedForProduction", "laboratoryOnly"
    };

    public static EuroOfficeSupervisedNativeOoxmlCandidate Parse(
        string? json, Guid expectedSession, Guid expectedTab,
        string expectedFixture, string expectedBinarySha256)
    {
        if (json is null || json.Length is < 2 or > 4096 ||
            expectedSession == Guid.Empty || expectedTab == Guid.Empty ||
            expectedSession == expectedTab || !ValidSha(expectedBinarySha256))
            throw new InvalidDataException("Unapproved supervised OOXML lab candidate.");

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = 8,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Supervised candidate must be an object.");

            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty field in root.EnumerateObject())
                if (!ExactFields.Contains(field.Name) || !found.Add(field.Name))
                    throw new InvalidDataException("Unknown or duplicate supervised candidate field.");

            if (found.Count != ExactFields.Count ||
                Text(root, "schema") != "getdoc-native-ooxml-supervised-candidate-lab/1" ||
                Text(root, "sessionId") != expectedSession.ToString("N") ||
                Text(root, "tabId") != expectedTab.ToString("N") ||
                Text(root, "fixtureName") != expectedFixture ||
                Text(root, "nativeBinarySha256") != expectedBinarySha256 ||
                !Flag(root, "nativeProcessExitZero") ||
                !Flag(root, "nativeBinaryPinMatched") ||
                !Flag(root, "stdoutSingleLineBounded") ||
                Flag(root, "nativeSaveProven") ||
                Flag(root, "documentVerified") ||
                Flag(root, "saveVerified") ||
                Flag(root, "approvedForProduction") ||
                !Flag(root, "laboratoryOnly"))
                throw new InvalidDataException("Supervised lab candidate is not eligible.");

            JsonElement nested = root.GetProperty("joinedCandidate");
            if (nested.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Missing nested independent OOXML candidate.");

            // The existing parser owns all nested fields, strict hashes and
            // cross-session checks. Never accept a downgraded native-only JSON.
            var joined = EuroOfficeNativeOoxmlJoinCandidate.Parse(
                nested.GetRawText(), expectedSession, expectedTab, expectedFixture);
            return new EuroOfficeSupervisedNativeOoxmlCandidate(
                expectedBinarySha256, joined);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Malformed supervised OOXML lab JSON.", ex);
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidDataException("Missing supervised candidate field.", ex);
        }
    }

    private static bool ValidSha(string? text) =>
        text is { Length: 64 } &&
        text.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Text(JsonElement root, string field)
    {
        JsonElement value = root.GetProperty(field);
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Supervised candidate string type mismatch.");
        return value.GetString() ?? "";
    }

    private static bool Flag(JsonElement root, string field) =>
        root.GetProperty(field).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException("Supervised candidate flag type mismatch.")
        };
}
