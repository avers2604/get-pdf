using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeRoundTripReceiptTests
{
    private static readonly Guid Session = Guid.Parse("10203040-5060-7080-90a0-b0c0d0e0f010");
    private static readonly Guid Tab = Guid.Parse("f010e0d0-c0b0-a090-8070-605040302010");
    private const string Fixture = "gd31j6_writer.docx";
    private static readonly string Candidate =
        "gd31j25-native-candidate-" + Session.ToString("N") + ".docx";
    private const string Old = "c80a1620d6c4883ad5f814dc9d1c1e54f24b99b9031b9ea60170b96a011e90eb";
    private const string New = "6cdb4fe20778fac05346b6046d001b54218fb19fb8919bd10107adfcbd0c90cb";

    private static string Payload(bool created = true, bool modified = true,
        bool notified = true, bool cancelled = false) =>
        JsonSerializer.Serialize(new
        {
            schema = "getdoc-synthetic-roundtrip-lab/1",
            sessionId = Session.ToString("N"), tabId = Tab.ToString("N"),
            fixtureName = Fixture,
            sourceBeforeSha256 = Old, sourceAfterSha256 = Old,
            modifiedNotification = modified, saveNotification = notified,
            saveCancelled = cancelled,
            candidateWritten = created,
            candidateName = created ? Candidate : "",
            candidateLength = created ? 146L : 0L,
            candidateSha256 = created ? New : "",
            documentVerified = false, saveVerified = false,
            pdfVerified = false, printApproved = false, laboratoryOnly = true
        });

    [TestMethod]
    public void CorrelatedNativeEventsOnlyProduceUntrustedCandidate()
    {
        var receipt = EuroOfficeNativeRoundTripReceipt.Parse(
            Payload(), Session, Tab, Fixture);
        Assert.IsTrue(receipt.CandidateWritten);
        Assert.AreEqual(Candidate, receipt.CandidateName);
        Assert.IsFalse(receipt.DocumentVerified);
        Assert.IsFalse(receipt.SaveCompleted);
        Assert.IsFalse(receipt.AuthorizedForPublication);
    }

    [TestMethod]
    public void NegativeNativeSaveNeverReportsCandidate()
    {
        var noSave = EuroOfficeNativeRoundTripReceipt.Parse(
            Payload(false, notified: false), Session, Tab, Fixture);
        Assert.IsFalse(noSave.CandidateWritten);
        Assert.IsFalse(noSave.SaveCompleted);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeRoundTripReceipt.Parse(
                Payload(created: true, modified: false), Session, Tab, Fixture));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeRoundTripReceipt.Parse(
                Payload(created: true, notified: false), Session, Tab, Fixture));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeRoundTripReceipt.Parse(
                Payload(created: true, cancelled: true), Session, Tab, Fixture));
    }

    [TestMethod]
    public void ForeignAndSpoofedEvidenceIsRefused()
    {
        string valid = Payload();
        foreach (string bad in new[]
        {
            valid.Replace("\"sourceAfterSha256\":\"" + Old, "\"sourceAfterSha256\":\"" + New),
            valid.Replace("\"saveVerified\":false", "\"saveVerified\":true"),
            valid.Replace("\"documentVerified\":false", "\"documentVerified\":true"),
            valid.Replace("\"candidateLength\":146", "\"candidateLength\":999999999"),
            valid.Replace("\"candidateName\":\"" + Candidate, "\"candidateName\":\"../escape.docx"),
            valid.Replace("\"schema\":", "\"schema\":\"duplicate\",\"schema\":"),
            valid.Replace("\"fixtureName\":", "\"arbitraryPath\":\"secret\",\"fixtureName\":"),
            valid.Replace("\"candidateSha256\":\"" + New, "\"candidateSha256\":\"bad"),
            valid.Replace("\"laboratoryOnly\":true", "\"laboratoryOnly\":false"),
            "{}"
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                EuroOfficeNativeRoundTripReceipt.Parse(bad, Session, Tab, Fixture));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeRoundTripReceipt.Parse(valid, Guid.NewGuid(), Tab, Fixture));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeRoundTripReceipt.Parse(valid, Session, Guid.NewGuid(), Fixture));
    }
}
