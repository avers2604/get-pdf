using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeSaveAsCandidateTests
{
    private static readonly Guid Session = Guid.Parse("d93b8c37-09b5-4a8c-9198-55cb4801f5a3");
    private static readonly Guid Tab = Guid.Parse("bdc14867-0768-4842-a8dc-1c45ef41b5aa");
    private const string Fixture = "gd31j6_writer.docx";
    private const string Hash = "c80a1620d6c4883ad5f814dc9d1c1e54f24b99b9031b9ea60170b96a011e90eb";

    private static string Receipt(bool published = true, bool callback = true,
        bool request = true, bool path = true, bool canceled = false,
        bool sourceUnchanged = true, bool verified = false) =>
        JsonSerializer.Serialize(new
        {
            schema = "getdoc-native-saveas-candidate-lab/1",
            sessionId = Session.ToString("N"),
            tabId = Tab.ToString("N"),
            fixtureName = Fixture,
            nativeSaveRequestObserved = request,
            savePathProvided = path,
            nativeOnSaveAfterAnswer = callback,
            saveCancelledAfterAnswer = canceled,
            sourceUnchanged,
            publishedToDisposableOutput = published,
            outputSha256 = published ? Hash : "",
            nativeSaveProven = verified,
            documentVerified = false,
            saveVerified = false,
            pdfVerified = false,
            printApproved = false,
            approvedForProduction = false,
            laboratoryOnly = true
        });

    [TestMethod]
    public void PublishedOutputAndNativeCallbackAreOnlyLaboratoryEvidence()
    {
        var result = EuroOfficeNativeSaveAsCandidate.Parse(Receipt(), Session, Tab, Fixture);
        Assert.IsTrue(result.NativeSaveRequestObserved);
        Assert.IsTrue(result.SavePathProvided);
        Assert.IsTrue(result.NativeOnSaveAfterAnswer);
        Assert.IsTrue(result.SourceUnchanged);
        Assert.IsTrue(result.PublishedToDisposableOutput);
        Assert.AreEqual(Hash, result.OutputSha256);
        Assert.IsFalse(result.NativeSaveProven);
        Assert.IsFalse(result.DocumentVerified);
        Assert.IsFalse(result.SaveVerified);
        Assert.IsFalse(result.ApprovedForProduction);
    }

    [TestMethod]
    public void RejectionOrAbsentNativeSaveDoesNotPromoteCandidate()
    {
        string json = Receipt(published: false, callback: false, request: false,
                              path: false, sourceUnchanged: false);
        var result = EuroOfficeNativeSaveAsCandidate.Parse(json, Session, Tab, Fixture);
        Assert.IsFalse(result.PublishedToDisposableOutput);
        Assert.IsFalse(result.NativeSaveProven);
    }

    [TestMethod]
    public void ForgedPrivilegeCrossSessionAndIncoherentStateFailClosed()
    {
        string valid = Receipt();
        foreach (string value in new[]
        {
            Receipt(verified: true),
            Receipt(published: true, callback: false),
            Receipt(published: true, request: false),
            Receipt(published: true, path: false),
            Receipt(published: true, canceled: true),
            Receipt(published: true, sourceUnchanged: false),
            Receipt(published: false),
            Receipt().Replace("\"documentVerified\":false", "\"documentVerified\":true"),
            Receipt().Replace("\"approvedForProduction\":false", "\"approvedForProduction\":true"),
            Receipt().Replace("\"laboratoryOnly\":true", "\"laboratoryOnly\":false"),
            Receipt().Replace("\"schema\":", "\"schema\":\"duplicate\",\"schema\":"),
            Receipt().Replace("\"fixtureName\":", "\"path\":\"secret.docx\",\"fixtureName\":"),
            Receipt().Replace(Hash, "NOT_SHA256"),
            "{}"
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                EuroOfficeNativeSaveAsCandidate.Parse(value, Session, Tab, Fixture));

        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeSaveAsCandidate.Parse(valid, Guid.NewGuid(), Tab, Fixture));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeSaveAsCandidate.Parse(valid, Session, Guid.NewGuid(), Fixture));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeSaveAsCandidate.Parse(valid, Session, Tab, "unapproved.docx"));
    }

    [TestMethod]
    public void OnlyThreeSyntheticFixturesCanBeParsed()
    {
        foreach (var name in new[]
        {
            "gd31j6_writer.docx", "gd31j6_calc.xlsx", "gd31j6_impress.pptx"
        })
        {
            string json = Receipt().Replace(Fixture, name);
            var result = EuroOfficeNativeSaveAsCandidate.Parse(json, Session, Tab, name);
            Assert.IsTrue(result.PublishedToDisposableOutput);
            Assert.IsFalse(result.NativeSaveProven);
        }
    }
}
