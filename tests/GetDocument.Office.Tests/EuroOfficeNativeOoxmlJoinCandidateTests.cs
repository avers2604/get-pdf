using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeOoxmlJoinCandidateTests
{
    private static readonly Guid Session = Guid.Parse("10203040-5060-7080-90a0-b0c0d0e0f010");
    private static readonly Guid Tab = Guid.Parse("f010e0d0-c0b0-a090-8070-605040302010");
    private const string Fixture = "gd31j6_writer.docx";
    private const string Source = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Output = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static string Receipt(bool correlated = true,
        bool package = true, bool canary = true, bool native = false) =>
        JsonSerializer.Serialize(new
        {
            schema = "getdoc-native-ooxml-joined-candidate-lab/1",
            sessionId = Session.ToString("N"),
            tabId = Tab.ToString("N"),
            fixtureName = Fixture,
            sourceSha256 = Source,
            outputSha256 = Output,
            nativeEventChainSelfReported = correlated,
            independentPackageVerified = package,
            syntheticCanaryVerified = canary,
            outputDigestCorrelated = true,
            sourceUnchanged = true,
            nativeSaveProven = native,
            documentVerified = false,
            saveVerified = false,
            approvedForProduction = false,
            laboratoryOnly = true
        });

    [TestMethod]
    public void JoinedLabEvidenceNeverPromotesProductionEditing()
    {
        var result = EuroOfficeNativeOoxmlJoinCandidate.Parse(
            Receipt(), Session, Tab, Fixture);
        Assert.IsTrue(result.NativeEventChainSelfReported);
        Assert.IsTrue(result.IndependentPackageVerified);
        Assert.IsTrue(result.SyntheticCanaryVerified);
        Assert.IsTrue(result.OutputDigestCorrelated);
        Assert.AreEqual(Source, result.SourceSha256);
        Assert.AreEqual(Output, result.OutputSha256);
        Assert.IsFalse(result.NativeSaveProven);
        Assert.IsFalse(result.DocumentVerified);
        Assert.IsFalse(result.SaveVerified);
        Assert.IsFalse(result.ApprovedForProduction);
    }

    [TestMethod]
    public void AllThreeSyntheticKindsAllowOnlyExplicitFixtureCorrelation()
    {
        foreach (string name in new[]
        {
            "gd31j6_writer.docx", "gd31j6_calc.xlsx", "gd31j6_impress.pptx"
        })
        {
            string payload = Receipt().Replace(Fixture, name);
            var parsed = EuroOfficeNativeOoxmlJoinCandidate.Parse(
                payload, Session, Tab, name);
            Assert.IsTrue(parsed.SyntheticCanaryVerified);
        }
    }

    [TestMethod]
    public void CrossSessionDuplicatedKeysSpoofedFlagsAndAlteredShaAreRejected()
    {
        string valid = Receipt();
        foreach (string payload in new[]
        {
            Receipt(correlated: false), Receipt(package: false),
            Receipt(canary: false), Receipt(native: true),
            valid.Replace("\"sourceUnchanged\":true", "\"sourceUnchanged\":false"),
            valid.Replace("\"outputDigestCorrelated\":true", "\"outputDigestCorrelated\":false"),
            valid.Replace("\"documentVerified\":false", "\"documentVerified\":true"),
            valid.Replace("\"saveVerified\":false", "\"saveVerified\":true"),
            valid.Replace("\"approvedForProduction\":false", "\"approvedForProduction\":true"),
            valid.Replace("\"laboratoryOnly\":true", "\"laboratoryOnly\":false"),
            valid.Replace("\"schema\":", "\"schema\":\"forged\",\"schema\":"),
            valid.Replace("\"fixtureName\":", "\"path\":\"corp.docx\",\"fixtureName\":"),
            valid.Replace(Source, Output),
            valid.Replace(Output, "not-a-sha"),
            valid.Replace("\"outputDigestCorrelated\":true", "\"outputDigestCorrelated\":1"),
            "{}"
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                EuroOfficeNativeOoxmlJoinCandidate.Parse(payload, Session, Tab, Fixture));

        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeOoxmlJoinCandidate.Parse(valid, Guid.NewGuid(), Tab, Fixture));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeOoxmlJoinCandidate.Parse(valid, Session, Guid.NewGuid(), Fixture));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeOoxmlJoinCandidate.Parse(valid, Session, Tab, "malicious.docx"));
    }
}
