using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// READ-ONLY interpretation of a GD-31J.25 synthetic ZIP/canary candidate.
/// The Python checker never authenticates the process which wrote the ZIP.
/// This type cannot grant editing, save, WORM, print or production capabilities.
/// </summary>
public sealed class EuroOfficeSyntheticRoundTripCandidate
{
    private EuroOfficeSyntheticRoundTripCandidate(string fixture,
        string sourceSha256, string outputSha256)
    {
        FixtureName = fixture;
        SourceSha256 = sourceSha256;
        OutputSha256 = outputSha256;
    }

    public string FixtureName { get; }
    public string SourceSha256 { get; }
    public string OutputSha256 { get; }
    public bool SyntheticPackageVerified => true;

    // A valid OOXML archive and an injected canary do not show which editor
    // wrote them. Native callbacks must be evaluated separately in a real VM.
    public bool NativeSaveProven => false;
    public bool DocumentVerified => false;
    public bool ApprovedForProduction => false;

    private static readonly HashSet<string> AllowedFixtures = new(StringComparer.Ordinal)
    {
        "gd31j6_writer.docx", "gd31j6_calc.xlsx", "gd31j6_impress.pptx"
    };

    private static readonly HashSet<string> Required = new(StringComparer.Ordinal)
    {
        "schema", "fixtureName", "sourceSha256", "outputSha256",
        "syntheticPackageVerified", "nativeSaveProven",
        "documentVerified", "approvedForProduction", "laboratoryOnly"
    };

    public static EuroOfficeSyntheticRoundTripCandidate Parse(
        string? json, string expectedFixture)
    {
        if (json is null || json.Length is < 2 or > 1024 ||
            !AllowedFixtures.Contains(expectedFixture))
            throw new InvalidDataException("Synthetic round-trip receipt is not approved.");

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = 4,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Round-trip receipt must be an object.");

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
                if (!Required.Contains(property.Name) || !names.Add(property.Name))
                    throw new InvalidDataException("Unexpected or duplicated round-trip field.");
            if (names.Count != Required.Count ||
                GetText(root, "schema") != "getdoc-synthetic-ooxml-roundtrip-candidate-lab/1" ||
                GetText(root, "fixtureName") != expectedFixture ||
                !GetFlag(root, "syntheticPackageVerified") ||
                GetFlag(root, "nativeSaveProven") ||
                GetFlag(root, "documentVerified") ||
                GetFlag(root, "approvedForProduction") ||
                !GetFlag(root, "laboratoryOnly"))
                throw new InvalidDataException("Forged, foreign or unverified synthetic receipt.");

            string source = GetText(root, "sourceSha256");
            string output = GetText(root, "outputSha256");
            if (!ValidSha256(source) || !ValidSha256(output) || source == output)
                throw new InvalidDataException("Invalid source/output SHA-256.");
            return new EuroOfficeSyntheticRoundTripCandidate(
                expectedFixture, source, output);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Malformed round-trip receipt.", ex);
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidDataException("Incomplete round-trip receipt.", ex);
        }
    }

    private static bool ValidSha256(string sha) =>
        sha.Length == 64 && sha.All(ch =>
            ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string GetText(JsonElement root, string name)
    {
        var field = root.GetProperty(name);
        if (field.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Round-trip text field has a wrong type.");
        return field.GetString() ?? string.Empty;
    }

    private static bool GetFlag(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException("Round-trip flag has a wrong type.")
        };
}
