using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// A bounded OBSERVATION, not authorization. A pinned CEF callback and an
/// exclusively published synthetic file cannot attest the editing process.
/// GD-31J.25 must independently validate OOXML/canary after this receipt.
/// </summary>
public sealed record EuroOfficeNativeSaveAsCandidate(
    bool NativeSaveRequestObserved,
    bool SavePathProvided,
    bool NativeOnSaveAfterAnswer,
    bool SourceUnchanged,
    bool PublishedToDisposableOutput,
    string OutputSha256)
{
    public bool NativeSaveProven => false;
    public bool DocumentVerified => false;
    public bool SaveVerified => false;
    public bool ApprovedForProduction => false;

    private static readonly HashSet<string> AllowedFixtures = new(StringComparer.Ordinal)
    {
        "gd31j6_writer.docx", "gd31j6_calc.xlsx", "gd31j6_impress.pptx"
    };
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "schema", "sessionId", "tabId", "fixtureName",
        "nativeSaveRequestObserved", "savePathProvided",
        "nativeOnSaveAfterAnswer", "saveCancelledAfterAnswer",
        "sourceUnchanged", "publishedToDisposableOutput", "outputSha256",
        "nativeSaveProven", "documentVerified", "saveVerified",
        "pdfVerified", "printApproved", "approvedForProduction", "laboratoryOnly"
    };

    public static EuroOfficeNativeSaveAsCandidate Parse(
        string? json, Guid expectedSession, Guid expectedTab, string expectedFixture)
    {
        if (json is null || json.Length is < 2 or > 2048 ||
            expectedSession == Guid.Empty || expectedTab == Guid.Empty ||
            !AllowedFixtures.Contains(expectedFixture))
            throw new InvalidDataException("Native SaveAs laboratory receipt is not approved.");

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Expected a native SaveAs JSON object.");
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
                if (!Fields.Contains(property.Name) || !found.Add(property.Name))
                    throw new InvalidDataException("Unexpected or repeated SaveAs field.");
            if (found.Count != Fields.Count ||
                Text(root, "schema") != "getdoc-native-saveas-candidate-lab/1" ||
                Text(root, "sessionId") != expectedSession.ToString("N") ||
                Text(root, "tabId") != expectedTab.ToString("N") ||
                Text(root, "fixtureName") != expectedFixture ||
                Flag(root, "nativeSaveProven") ||
                Flag(root, "documentVerified") ||
                Flag(root, "saveVerified") ||
                Flag(root, "pdfVerified") ||
                Flag(root, "printApproved") ||
                Flag(root, "approvedForProduction") ||
                !Flag(root, "laboratoryOnly"))
                throw new InvalidDataException("Forged, wrong-session or privileged SaveAs receipt.");

            bool request = Flag(root, "nativeSaveRequestObserved");
            bool path = Flag(root, "savePathProvided");
            bool callback = Flag(root, "nativeOnSaveAfterAnswer");
            bool canceled = Flag(root, "saveCancelledAfterAnswer");
            bool source = Flag(root, "sourceUnchanged");
            bool published = Flag(root, "publishedToDisposableOutput");
            string sha = Text(root, "outputSha256");
            if ((path && !request) || (callback && !path) ||
                (canceled && !path) || (callback && canceled) ||
                (published && !(request && path && callback && source && !canceled)) ||
                (published ? !ValidSha(sha) : sha.Length != 0))
                throw new InvalidDataException("Invalid SaveAs correlation or output SHA.");

            return new EuroOfficeNativeSaveAsCandidate(
                request, path, callback && !canceled, source, published, sha);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Malformed Native SaveAs receipt.", ex);
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidDataException("Incomplete Native SaveAs receipt.", ex);
        }
    }

    private static bool ValidSha(string sha) =>
        sha.Length == 64 && sha.All(ch =>
            ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Text(JsonElement root, string name)
    {
        JsonElement field = root.GetProperty(name);
        if (field.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Wrong native SaveAs text field.");
        return field.GetString() ?? string.Empty;
    }

    private static bool Flag(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException("Wrong native SaveAs boolean field.")
        };
}
