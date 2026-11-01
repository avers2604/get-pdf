using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeSupervisedNativeOoxmlCandidateTests
{
    private static readonly Guid Session = Guid.Parse("10203040-5060-7080-90a0-b0c0d0e0f010");
    private static readonly Guid Tab = Guid.Parse("f010e0d0-c0b0-a090-8070-605040302010");
    private const string Fixture = "gd31j6_writer.docx";
    private const string PinnedBinary = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string Source = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Output = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static string Receipt() =>
        JsonSerializer.Serialize(new
        {
            schema = "getdoc-native-ooxml-joined-candidate-lab/1",
            sessionId = Session.ToString("N"),
            tabId = Tab.ToString("N"),
            fixtureName = Fixture,
            sourceSha256 = Source,
            outputSha256 = Output,
            nativeEventChainSelfReported = true,
            independentPackageVerified = true,
            syntheticCanaryVerified = true,
            outputDigestCorrelated = true,
            sourceUnchanged = true,
            nativeSaveProven = false,
            documentVerified = false,
            saveVerified = false,
            approvedForProduction = false,
            laboratoryOnly = true
        });

    private static string Supervised()
    {
        using var joined = JsonDocument.Parse(Receipt());
        return JsonSerializer.Serialize(new
        {
            schema = "getdoc-native-ooxml-supervised-candidate-lab/1",
            sessionId = Session.ToString("N"),
            tabId = Tab.ToString("N"),
            fixtureName = Fixture,
            nativeBinarySha256 = PinnedBinary,
            nativeProcessExitZero = true,
            nativeBinaryPinMatched = true,
            stdoutSingleLineBounded = true,
            joinedCandidate = joined.RootElement,
            nativeSaveProven = false,
            documentVerified = false,
            saveVerified = false,
            approvedForProduction = false,
            laboratoryOnly = true
        });
    }

    [TestMethod]
    public void SupervisedEnvelopeIsDiagnosticOnly()
    {
        var result = EuroOfficeSupervisedNativeOoxmlCandidate.Parse(
            Supervised(), Session, Tab, Fixture, PinnedBinary);
        Assert.AreEqual(PinnedBinary, result.NativeBinarySha256);
        Assert.AreEqual(Source, result.Joined.SourceSha256);
        Assert.AreEqual(Output, result.Joined.OutputSha256);
        Assert.IsFalse(result.NativeSaveProven);
        Assert.IsFalse(result.DocumentVerified);
        Assert.IsFalse(result.SaveVerified);
        Assert.IsFalse(result.ApprovedForProduction);
    }

    [TestMethod]
    public void OuterPinSessionFlagsAndNestedCandidateMustAllMatch()
    {
        string valid = Supervised();
        foreach (string invalid in new[]
        {
            valid.Replace(PinnedBinary, "not-a-pin"),
            valid.Replace(PinnedBinary, "d" + PinnedBinary[1..]),
            valid.Replace("\"nativeProcessExitZero\":true", "\"nativeProcessExitZero\":false"),
            valid.Replace("\"nativeBinaryPinMatched\":true", "\"nativeBinaryPinMatched\":false"),
            valid.Replace("\"stdoutSingleLineBounded\":true", "\"stdoutSingleLineBounded\":1"),
            valid.Replace("\"nativeSaveProven\":false", "\"nativeSaveProven\":true"),
            valid.Replace("\"approvedForProduction\":false", "\"approvedForProduction\":true"),
            valid.Replace("\"schema\":", "\"schema\":\"duplicate\",\"schema\":"),
            valid.Replace("\"fixtureName\":", "\"foreignPath\":\"secret\",\"fixtureName\":"),
            valid.Replace("\"joinedCandidate\":{", "\"joinedCandidate\":null,\"junk\":{"),
            valid.Replace("\"sourceSha256\":\"" + Source, "\"sourceSha256\":\"" + Output),
            "{}"
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                EuroOfficeSupervisedNativeOoxmlCandidate.Parse(
                    invalid, Session, Tab, Fixture, PinnedBinary));

        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSupervisedNativeOoxmlCandidate.Parse(
                valid, Guid.NewGuid(), Tab, Fixture, PinnedBinary));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSupervisedNativeOoxmlCandidate.Parse(
                valid, Session, Tab, "private.docx", PinnedBinary));
    }
}
