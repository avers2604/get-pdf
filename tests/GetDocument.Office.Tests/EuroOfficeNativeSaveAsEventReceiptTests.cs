using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeSaveAsEventReceiptTests
{
    private static readonly Guid Session = Guid.Parse("10203040-5060-7080-90a0-b0c0d0e0f010");
    private static readonly Guid Tab = Guid.Parse("f010e0d0-c0b0-a090-8070-605040302010");
    private const string Fixture = "gd31j6_writer.docx";
    private const string Original = "c80a1620d6c4883ad5f814dc9d1c1e54f24b99b9031b9ea60170b96a011e90eb";
    private const string Changed = "6cdb4fe20778fac05346b6046d001b54218fb19fb8919bd10107adfcbd0c90cb";
    private static string Core(bool candidate) => JsonSerializer.Serialize(new
    {
        schema = "getdoc-native-saveas-private-lab/1",
        sessionId = Session.ToString("N"), tabId = Tab.ToString("N"),
        fixtureName = Fixture, selectedFormat = "docx",
        sourceBeforeSha256 = Original, sourceAfterSha256 = Original,
        saveAsRequestSeen = true, saveAsTargetAccepted = true,
        saveNotification = candidate, saveCancelled = false,
        candidateWritten = candidate,
        candidateName = candidate ? EuroOfficeNativeSaveAsReceipt.CandidateNameFor(Session, "docx") : "",
        candidateLength = candidate ? 1024L : 0L,
        candidateSha256 = candidate ? Changed : "",
        documentVerified = false, saveVerified = false,
        pdfVerified = false, printApproved = false, laboratoryOnly = true
    });

    private static string Envelope(string status = "candidate_untrusted",
        bool candidate = true, int dialogCount = 1, int saveCount = 1,
        int dialogSeq = 1, int saveSeq = 2) => JsonSerializer.Serialize(new
    {
        schema = "getdoc-native-saveas-event-lab/2",
        sessionId = Session.ToString("N"), tabId = Tab.ToString("N"),
        fixtureName = Fixture, failureCode = status,
        phase = candidate ? "quarantined" : "target_assigned",
        dialogCount, onSaveCount = saveCount,
        dialogSequence = dialogSeq, onSaveSequence = saveSeq,
        receipt = JsonDocument.Parse(Core(candidate)).RootElement
    });

    [TestMethod]
    public void CorrelatedNativeEventsGiveOnlyUntrustedCandidate()
    {
        var report = EuroOfficeNativeSaveAsEventReceipt.Parse(
            Envelope(), Session, Tab, Fixture, "docx");
        Assert.AreEqual("candidate_untrusted", report.FailureCode);
        Assert.AreEqual(1, report.DialogCount);
        Assert.IsTrue(report.CorrelatedNativeCallbacks);
        Assert.IsFalse(report.SaveCompleted);
        Assert.IsFalse(report.AuthorizedForPublication);
        Assert.AreEqual(EuroOfficeNativeSaveAsLifecycle.State.SavedCandidateValidatedButUntrusted,
            EuroOfficeNativeSaveAsLifecycle.Classify(report, true).Status);
    }

    [TestMethod]
    public void DuplicateOrCancelledOrTimeoutNeverBecomesValidatedSave()
    {
        var duplicate = EuroOfficeNativeSaveAsEventReceipt.Parse(
            Envelope("duplicate_dialog", false, 2, 0, 1, 0),
            Session, Tab, Fixture, "docx");
        Assert.IsFalse(duplicate.CorrelatedNativeCallbacks);
        Assert.AreEqual(EuroOfficeNativeSaveAsLifecycle.State.DuplicateDialog,
            EuroOfficeNativeSaveAsLifecycle.Classify(duplicate, false).Status);
        var timedOut = EuroOfficeNativeSaveAsEventReceipt.Parse(
            Envelope("timeout", false, 1, 0, 1, 0),
            Session, Tab, Fixture, "docx");
        Assert.AreEqual(EuroOfficeNativeSaveAsLifecycle.State.Timeout,
            EuroOfficeNativeSaveAsLifecycle.Classify(timedOut, false).Status);
    }

    [TestMethod]
    public void LateDuplicateAfterAssignedTargetRemainsParseableAsUnsafeFailure()
    {
        // Regression: the real native host previously erased the observed
        // first request when count became 2. That made its nested /1 receipt
        // impossible (target=true but request=false) and hid DuplicateDialog.
        var receipt = EuroOfficeNativeSaveAsEventReceipt.Parse(
            Envelope("duplicate_dialog", candidate: false,
                dialogCount: 2, saveCount: 0, dialogSeq: 1, saveSeq: 0),
            Session, Tab, Fixture, "docx");
        Assert.IsTrue(receipt.Core.SaveAsRequestSeen);
        Assert.IsTrue(receipt.Core.SaveAsTargetAccepted);
        Assert.AreEqual(2, receipt.DialogCount);
        Assert.IsFalse(receipt.Core.CandidateWritten);
        Assert.IsFalse(receipt.CorrelatedNativeCallbacks);
        Assert.AreEqual(EuroOfficeNativeSaveAsLifecycle.State.DuplicateDialog,
            EuroOfficeNativeSaveAsLifecycle.Classify(receipt, false).Status);
        Assert.IsFalse(receipt.AuthorizedForPublication);
    }

    [TestMethod]
    public void InvalidDialogAfterAcceptedRequestStillCannotAuthoriseCandidate()
    {
        // An extra SDK dialog is a failure EVEN if the first reply was
        // targeted. Test data must not imply a successful second Save As.
        var failed = EuroOfficeNativeSaveAsEventReceipt.Parse(
            Envelope("duplicate_dialog", candidate: false,
                dialogCount: 2, saveCount: 0, dialogSeq: 1, saveSeq: 0),
            Session, Tab, Fixture, "docx");
        Assert.AreEqual("duplicate_dialog", failed.FailureCode);
        Assert.IsFalse(failed.SaveCompleted);
        Assert.IsFalse(failed.DocumentVerified);
        Assert.IsFalse(failed.AuthorizedForPublication);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeSaveAsEventReceipt.Parse(
                Envelope("candidate_untrusted", candidate: true, dialogCount: 2),
                Session, Tab, Fixture, "docx"));
    }

    [TestMethod]
    public void RejectsForgedOrInconsistentOrderAndUnrecognizedMetadata()
    {
        string valid = Envelope();
        foreach (string bad in new[]
        {
            Envelope("candidate_untrusted", true, 2),
            Envelope("candidate_untrusted", true, 1, 0),
            Envelope("candidate_untrusted", true, 1, 1, 4, 2),
            Envelope("candidate_untrusted", false),
            Envelope("unknown_event", false),
            valid.Replace("\"failureCode\":", "\"failureCode\":\"duplicate\",\"failureCode\":"),
            valid.Replace("\"schema\":", "\"schema\":\"foreign\",\"schema\":"),
            valid.Replace("\"phase\":\"quarantined\"", "\"phase\":\"published\""),
            valid.Replace("\"dialogCount\":1", "\"dialogCount\":500"),
            valid.Replace("\"laboratoryOnly\":true", "\"laboratoryOnly\":false"),
            valid.Replace("\"saveVerified\":false", "\"saveVerified\":true")
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                EuroOfficeNativeSaveAsEventReceipt.Parse(bad, Session, Tab, Fixture, "docx"));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeSaveAsEventReceipt.Parse(valid, Guid.NewGuid(), Tab, Fixture, "docx"));
    }

    [TestMethod]
    public void RejectsLegacyReceiptInNewNativeRunPath()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeSaveAsEventReceipt.Parse(Core(true), Session, Tab, Fixture, "docx"));
    }
}
