using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeConversionCandidateTests
{
    private static readonly Guid Session = Guid.Parse("01020304-0506-0708-090a-0b0c0d0e0f10");
    private static readonly Guid Tab = Guid.Parse("f0e0d0c0-b0a0-9080-7060-504030201000");
    private const string Fixture = "gd31j6_writer.docx";
    private const string Hash = "c80a1620d6c4883ad5f814dc9d1c1e54f24b99b9031b9ea60170b96a011e90eb";

    private static string Receipt(bool observed, bool view = true, bool path = true,
        long length = 65536, string? sha = null) => JsonSerializer.Serialize(new
        {
            schema = "getdoc-native-conversion-candidate-lab/1",
            sessionId = Session.ToString("N"),
            tabId = Tab.ToString("N"),
            fixtureName = Fixture,
            cefViewLoaded = view,
            editorPathMatched = path,
            conversionArtifactObserved = observed,
            editorBinaryLength = observed ? length : 0,
            editorBinarySha256 = observed ? sha ?? Hash : "",
            documentVerified = false,
            saveVerified = false,
            pdfVerified = false,
            printApproved = false,
            laboratoryOnly = true
        });

    [TestMethod]
    public void VerifiedCandidateMetadataStillCannotAuthorizeEditingOrSaving()
    {
        var parsed = EuroOfficeNativeConversionCandidate.Parse(Receipt(true), Session, Tab, Fixture);
        Assert.IsTrue(parsed.ConversionArtifactObserved);
        Assert.AreEqual(65536L, parsed.EditorBinaryLength);
        Assert.AreEqual(Hash, parsed.EditorBinarySha256);
        Assert.IsFalse(parsed.DocumentVerified);
        Assert.IsFalse(parsed.EditReady);
        Assert.IsFalse(parsed.SaveCompleted);
        Assert.IsFalse(parsed.PdfReady);
        Assert.IsFalse(parsed.ApprovedForProduction);
    }

    [TestMethod]
    public void RejectsForeignSessionsAndDifferentFixture()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeConversionCandidate.Parse(Receipt(true), Guid.NewGuid(), Tab, Fixture));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeConversionCandidate.Parse(Receipt(true), Session, Guid.NewGuid(), Fixture));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeConversionCandidate.Parse(Receipt(true), Session, Tab, "gd31j6_calc.xlsx"));
    }

    [TestMethod]
    public void RejectsForgedOrInconsistentArtifactEvidence()
    {
        var valid = Receipt(true);
        foreach (var json in new[]
        {
            Receipt(true, view: false),
            Receipt(true, path: false),
            Receipt(true, length: 0),
            Receipt(true, length: 128L * 1024 * 1024 + 1),
            Receipt(true, sha: "abcdef"),
            Receipt(true, sha: new string('X', 64)),
            Receipt(false).Replace("\"editorBinaryLength\":0", "\"editorBinaryLength\":32"),
            valid.Replace("\"saveVerified\":false", "\"saveVerified\":true"),
            valid.Replace("\"documentVerified\":false", "\"documentVerified\":true"),
            valid.Replace("\"laboratoryOnly\":true", "\"laboratoryOnly\":false"),
            valid.Replace("\"schema\":", "\"schema\":\"duplicate\",\"schema\":"),
            valid.Replace("\"fixtureName\":", "\"hostPath\":\"/secret\",\"fixtureName\":"),
            "{}",
            new string('z', 4096)
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                EuroOfficeNativeConversionCandidate.Parse(json, Session, Tab, Fixture));
    }

    [TestMethod]
    public void NegativeEvidenceHasNoArtifactDigestOrPermission()
    {
        var result = EuroOfficeNativeConversionCandidate.Parse(Receipt(false), Session, Tab, Fixture);
        Assert.IsFalse(result.ConversionArtifactObserved);
        Assert.AreEqual(0L, result.EditorBinaryLength);
        Assert.AreEqual("", result.EditorBinarySha256);
        Assert.IsFalse(result.EditReady);
    }
}
