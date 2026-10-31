using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// Bounded synthetic-only correlation of an untrusted native receipt with an
/// independent read-only OOXML/canary check. This is NOT proof that EuroOffice
/// produced the bytes. Never wire into the user launcher, WORM or print.
/// </summary>
public sealed class EuroOfficeNativeOoxmlJoinCandidate
{
    private EuroOfficeNativeOoxmlJoinCandidate(
        string fixtureName, string sourceSha256, string outputSha256)
    {
        FixtureName = fixtureName;
        SourceSha256 = sourceSha256;
        OutputSha256 = outputSha256;
    }

    public string FixtureName { get; }
    public string SourceSha256 { get; }
    public string OutputSha256 { get; }
    public bool NativeEventChainSelfReported => true;
    public bool IndependentPackageVerified => true;
    public bool SyntheticCanaryVerified => true;
    public bool OutputDigestCorrelated => true;
    public bool SourceUnchanged => true;

    // No cryptographic binding of a native process and its file contents.
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
        "schema", "sessionId", "tabId", "fixtureName", "sourceSha256",
        "outputSha256", "nativeEventChainSelfReported",
        "independentPackageVerified", "syntheticCanaryVerified",
        "outputDigestCorrelated", "sourceUnchanged",
        "nativeSaveProven", "documentVerified", "saveVerified",
        "approvedForProduction", "laboratoryOnly"
    };

    public static EuroOfficeNativeOoxmlJoinCandidate Parse(
        string? json, Guid expectedSession, Guid expectedTab,
        string expectedFixture)
    {
        if (json is null || json.Length is < 2 or > 2048 ||
            expectedSession == Guid.Empty || expectedTab == Guid.Empty ||
            expectedSession == expectedTab ||
            !AllowedFixtures.Contains(expectedFixture))
            throw new InvalidDataException("Joined OOXML candidate is not approved.");

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = 4,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Joined receipt must be a JSON object.");

            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
                if (!ExactFields.Contains(property.Name) || !found.Add(property.Name))
                    throw new InvalidDataException("Extra or duplicate joined receipt key.");

            if (found.Count != ExactFields.Count ||
                Text(root, "schema") != "getdoc-native-ooxml-joined-candidate-lab/1" ||
                Text(root, "sessionId") != expectedSession.ToString("N") ||
                Text(root, "tabId") != expectedTab.ToString("N") ||
                Text(root, "fixtureName") != expectedFixture ||
                !Flag(root, "nativeEventChainSelfReported") ||
                !Flag(root, "independentPackageVerified") ||
                !Flag(root, "syntheticCanaryVerified") ||
                !Flag(root, "outputDigestCorrelated") ||
                !Flag(root, "sourceUnchanged") ||
                Flag(root, "nativeSaveProven") ||
                Flag(root, "documentVerified") ||
                Flag(root, "saveVerified") ||
                Flag(root, "approvedForProduction") ||
                !Flag(root, "laboratoryOnly"))
                throw new InvalidDataException("Forged joined evidence or cross-session receipt.");

            string source = Text(root, "sourceSha256");
            string output = Text(root, "outputSha256");
            if (!ShaValid(source) || !ShaValid(output) || source == output)
                throw new InvalidDataException("Joined source/output hashes are invalid.");
            return new EuroOfficeNativeOoxmlJoinCandidate(
                expectedFixture, source, output);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Malformed joined OOXML JSON.", ex);
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidDataException("Missing required joined OOXML field.", ex);
        }
    }

    private static bool ShaValid(string value) =>
        value.Length == 64 && value.All(ch =>
            ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Text(JsonElement root, string field)
    {
        JsonElement value = root.GetProperty(field);
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Joined receipt text field type invalid.");
        return value.GetString() ?? "";
    }

    private static bool Flag(JsonElement root, string field) =>
        root.GetProperty(field).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException("Joined receipt flag type invalid.")
        };
}
