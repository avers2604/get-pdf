using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// GD-31J.27d: strictly scoped OFFLINE synthetic v2 SaveAs candidate.
/// Two file hashes and process exit are local observations, NOT ld.so/CEF
/// module attestation, native origin proof or permission to write user files.
/// </summary>
public sealed class EuroOfficeSupervisedPinnedV2Candidate
{
    private EuroOfficeSupervisedPinnedV2Candidate(
        string binarySha, string sourceSha, string outputSha)
    {
        NativeBinarySha256 = binarySha;
        SourceSha256 = sourceSha;
        OutputSha256 = outputSha;
    }

    public string NativeBinarySha256 { get; }
    public string SourceSha256 { get; }
    public string OutputSha256 { get; }
    public bool IndependentOoxmlCorrelated => true;
    public bool NativeSaveProven => false;
    public bool DocumentVerified => false;
    public bool SaveVerified => false;
    public bool ApprovedForProduction => false;

    private static readonly HashSet<string> Fixtures = new(StringComparer.Ordinal)
    {
        "gd31j6_writer.docx", "gd31j6_calc.xlsx", "gd31j6_impress.pptx"
    };
    private static readonly HashSet<string> OuterKeys = new(StringComparer.Ordinal)
    {
        "schema", "sessionId", "tabId", "fixtureName",
        "nativeBinarySha256", "runtimeLibrarySha256",
        "nativeProcessExitZero", "runtimeLibraryPinsMatched",
        "stdoutSingleLineBounded", "joinedCandidate",
        "nativeSaveProven", "documentVerified", "saveVerified",
        "approvedForProduction", "laboratoryOnly"
    };
    private static readonly HashSet<string> RuntimeKeys = new(StringComparer.Ordinal)
    {
        "ascdocumentscore", "qtascdocumentscore"
    };
    private static readonly HashSet<string> NestedKeys = new(StringComparer.Ordinal)
    {
        "schema", "sessionId", "tabId", "fixtureName", "failureCode",
        "nativeEventOrderSelfReported", "outputSha256", "sourceSha256",
        "independentOoxmlCorrelated", "nativeSaveProven", "documentVerified",
        "saveVerified", "approvedForProduction", "laboratoryOnly"
    };

    public static EuroOfficeSupervisedPinnedV2Candidate Parse(
        string? raw, Guid expectedSession, Guid expectedTab, string expectedFixture,
        string expectedBinarySha, string expectedAscSha, string expectedQtAscSha)
    {
        if (raw is null || raw.Length is < 2 or > 4096 ||
            expectedSession == Guid.Empty || expectedTab == Guid.Empty ||
            expectedSession == expectedTab || !Fixtures.Contains(expectedFixture) ||
            !ValidSha(expectedBinarySha) || !ValidSha(expectedAscSha) ||
            !ValidSha(expectedQtAscSha))
            throw new InvalidDataException("Invalid pinned v2 laboratory scope or reviewed pins.");

        try
        {
            using JsonDocument document = JsonDocument.Parse(raw, new JsonDocumentOptions
            {
                MaxDepth = 6, AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            JsonElement root = document.RootElement;
            Exact(root, OuterKeys);
            Scope(root, "getdoc-native-pinned-v2-supervised-ooxml-lab/1",
                expectedSession, expectedTab, expectedFixture);
            if (Text(root, "nativeBinarySha256") != expectedBinarySha ||
                !Flag(root, "nativeProcessExitZero") ||
                !Flag(root, "runtimeLibraryPinsMatched") ||
                !Flag(root, "stdoutSingleLineBounded"))
                throw new InvalidDataException("Binary pin or bounded native v2 process claim mismatch.");
            DeniedPrivileges(root);

            JsonElement libraryPins = root.GetProperty("runtimeLibrarySha256");
            Exact(libraryPins, RuntimeKeys);
            if (Text(libraryPins, "ascdocumentscore") != expectedAscSha ||
                Text(libraryPins, "qtascdocumentscore") != expectedQtAscSha)
                throw new InvalidDataException("Native runtime-library pin mismatch.");

            JsonElement candidate = root.GetProperty("joinedCandidate");
            Exact(candidate, NestedKeys);
            Scope(candidate, "getdoc-native-pinned-v2-ooxml-joined-lab/1",
                expectedSession, expectedTab, expectedFixture);
            DeniedPrivileges(candidate);
            if (Text(candidate, "failureCode") != "candidate_untrusted" ||
                !Flag(candidate, "nativeEventOrderSelfReported") ||
                !Flag(candidate, "independentOoxmlCorrelated"))
                throw new InvalidDataException("Pinned v2 candidate is not independently correlated.");

            string source = Text(candidate, "sourceSha256");
            string output = Text(candidate, "outputSha256");
            if (!ValidSha(source) || !ValidSha(output) || source == output)
                throw new InvalidDataException("Pinned v2 source/output SHA invalid.");

            return new EuroOfficeSupervisedPinnedV2Candidate(
                expectedBinarySha, source, output);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Malformed pinned v2 supervisor JSON.", ex);
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidDataException("Missing pinned v2 supervisor JSON field.", ex);
        }
    }

    private static void Scope(JsonElement item, string schema, Guid session,
        Guid tab, string fixture)
    {
        if (Text(item, "schema") != schema ||
            Text(item, "sessionId") != session.ToString("N") ||
            Text(item, "tabId") != tab.ToString("N") ||
            Text(item, "fixtureName") != fixture)
            throw new InvalidDataException("Pinned v2 supervisor schema/session mismatch.");
    }

    private static void DeniedPrivileges(JsonElement item)
    {
        if (Flag(item, "nativeSaveProven") ||
            Flag(item, "documentVerified") ||
            Flag(item, "saveVerified") ||
            Flag(item, "approvedForProduction") ||
            !Flag(item, "laboratoryOnly"))
            throw new InvalidDataException("Forged native v2 production authority.");
    }

    private static void Exact(JsonElement item, HashSet<string> required)
    {
        if (item.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Pinned v2 supervisor requires JSON object.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty prop in item.EnumerateObject())
            if (!required.Contains(prop.Name) || !found.Add(prop.Name))
                throw new InvalidDataException("Unexpected or duplicate pinned v2 JSON key.");
        if (found.Count != required.Count)
            throw new InvalidDataException("Missing pinned v2 supervisor JSON key.");
    }

    private static string Text(JsonElement obj, string key)
    {
        JsonElement value = obj.GetProperty(key);
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Pinned v2 supervisor text field has wrong type.");
        return value.GetString() ?? string.Empty;
    }

    private static bool Flag(JsonElement obj, string key) =>
        obj.GetProperty(key).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException("Pinned v2 supervisor flag has wrong type.")
        };

    private static bool ValidSha(string? value) =>
        value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
