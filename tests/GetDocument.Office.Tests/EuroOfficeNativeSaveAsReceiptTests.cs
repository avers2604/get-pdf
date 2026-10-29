using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeSaveAsReceiptTests
{
    private static readonly Guid Session = Guid.Parse("10203040-5060-7080-90a0-b0c0d0e0f010");
    private static readonly Guid Tab = Guid.Parse("f010e0d0-c0b0-a090-8070-605040302010");
    private const string Fixture = "gd31j6_writer.docx";
    private const string OldHash = "c80a1620d6c4883ad5f814dc9d1c1e54f24b99b9031b9ea60170b96a011e90eb";
    private const string NewHash = "6cdb4fe20778fac05346b6046d001b54218fb19fb8919bd10107adfcbd0c90cb";
    private static readonly string Candidate = "gd31j26-saveas-candidate-" + Session.ToString("N") + ".docx";

    private static string Receipt(bool dialog = true, bool target = true, bool saved = true,
        bool cancelled = false, bool created = true) => JsonSerializer.Serialize(new
        {
            schema = "getdoc-native-saveas-private-lab/1",
            sessionId = Session.ToString("N"), tabId = Tab.ToString("N"),
            fixtureName = Fixture, selectedFormat = "docx",
            sourceBeforeSha256 = OldHash, sourceAfterSha256 = OldHash,
            saveAsRequestSeen = dialog, saveAsTargetAccepted = target,
            saveNotification = saved, saveCancelled = cancelled,
            candidateWritten = created,
            candidateName = created ? Candidate : "",
            candidateLength = created ? 321L : 0L,
            candidateSha256 = created ? NewHash : "",
            documentVerified = false, saveVerified = false, pdfVerified = false,
            printApproved = false, laboratoryOnly = true
        });

    [TestMethod]
    public void NativeSaveAsDialogAndPrivateTargetAreNotPublicationAuthority()
    {
        var parsed = EuroOfficeNativeSaveAsReceipt.Parse(Receipt(), Session, Tab, Fixture, "docx");
        Assert.IsTrue(parsed.SaveAsRequestSeen);
        Assert.IsTrue(parsed.SaveAsTargetAccepted);
        Assert.IsTrue(parsed.CandidateWritten);
        Assert.IsFalse(parsed.SaveCompleted);
        Assert.IsFalse(parsed.DocumentVerified);
        Assert.IsFalse(parsed.AuthorizedForPublication);
    }

    [TestMethod]
    public void RejectsSaveAsWithoutRequestOrAcceptedTargetOrOnSave()
    {
        foreach (var json in new[]
        {
            Receipt(dialog: false), Receipt(target: false),
            Receipt(saved: false), Receipt(cancelled: true)
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                EuroOfficeNativeSaveAsReceipt.Parse(json, Session, Tab, Fixture, "docx"));
    }

    [TestMethod]
    public void AnAbsentCandidateHasNoPrivileges()
    {
        var parsed = EuroOfficeNativeSaveAsReceipt.Parse(Receipt(created: false),
            Session, Tab, Fixture, "docx");
        Assert.IsFalse(parsed.CandidateWritten);
        Assert.IsFalse(parsed.SaveCompleted);
    }

    [TestMethod]
    public void StrictlyRejectsForgedFieldsCrossSessionAndCrossFormat()
    {
        string valid = Receipt();
        foreach (var bad in new[]
        {
            valid.Replace("\"saveVerified\":false", "\"saveVerified\":true"),
            valid.Replace("\"documentVerified\":false", "\"documentVerified\":true"),
            valid.Replace("\"schema\":", "\"schema\":\"other\",\"schema\":"),
            valid.Replace("\"fixtureName\":", "\"hostPath\":\"/tmp/escape\",\"fixtureName\":"),
            valid.Replace("\"candidateName\":\"" + Candidate, "\"candidateName\":\"../secret.docx"),
            valid.Replace("\"selectedFormat\":\"docx\"", "\"selectedFormat\":\"xlsx\""),
            valid.Replace("\"sourceAfterSha256\":\"" + OldHash, "\"sourceAfterSha256\":\"" + NewHash),
            valid.Replace("\"candidateSha256\":\"" + NewHash, "\"candidateSha256\":\"abcd"),
            valid.Replace("\"candidateLength\":321", "\"candidateLength\":999999999"),
            valid.Replace("\"laboratoryOnly\":true", "\"laboratoryOnly\":false")
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                EuroOfficeNativeSaveAsReceipt.Parse(bad, Session, Tab, Fixture, "docx"));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeSaveAsReceipt.Parse(valid, Guid.NewGuid(), Tab, Fixture, "docx"));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeSaveAsReceipt.Parse(valid, Session, Tab, Fixture, "xlsx"));
    }

    [TestMethod]
    public void RejectUnsupportedCrossFormatRequestsBeforeProcessLaunch()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            EuroOfficeNativeSaveAsReceipt.ValidateFormat(Fixture, "pptx"));
        Assert.AreEqual("xlsx",
            EuroOfficeNativeSaveAsReceipt.ValidateFormat("gd31j6_calc.xlsx", "xlsx"));
    }
}
