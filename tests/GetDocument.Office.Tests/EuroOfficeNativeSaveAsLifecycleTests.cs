using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeSaveAsLifecycleTests
{
    private static readonly Guid Session = Guid.Parse("10203040-5060-7080-90a0-b0c0d0e0f010");
    private static readonly Guid Tab = Guid.Parse("f010e0d0-c0b0-a090-8070-605040302010");
    private static EuroOfficeNativeSaveAsReceipt Receipt(
        bool request = true, bool target = true, bool ack = true,
        bool cancelled = false, bool candidate = true) => new(
        Session, Tab, "gd31j6_writer.docx", "docx",
        new string('a', 64), new string('a', 64),
        request, target, ack, cancelled, candidate,
        candidate ? "gd31j26-saveas-candidate-" + Session.ToString("N") + ".docx" : "",
        candidate ? 2048 : 0, candidate ? new string('b', 64) : "");

    [TestMethod]
    public void EvenValidatedPrivateOoxmlNeverBecomesPublicationOrSavePermission()
    {
        var result = EuroOfficeNativeSaveAsLifecycle.Classify(Receipt(), candidateValidated: true);
        Assert.AreEqual(EuroOfficeNativeSaveAsLifecycle.State.SavedCandidateValidatedButUntrusted,
            result.Status);
        Assert.IsFalse(result.DocumentVerified);
        Assert.IsFalse(result.SaveCompleted);
        Assert.IsFalse(result.AuthorizedForPublication);
    }

    [TestMethod]
    public void ReportsExactDiagnosticFailureWithoutPretendingThatAFileWasSaved()
    {
        var cases = new[]
        {
            (Receipt(request: false, target: false, ack: false, candidate: false),
                EuroOfficeNativeSaveAsLifecycle.State.DialogNotObserved),
            (Receipt(target: false, ack: false, candidate: false),
                EuroOfficeNativeSaveAsLifecycle.State.TargetNotAccepted),
            (Receipt(cancelled: true, candidate: false),
                EuroOfficeNativeSaveAsLifecycle.State.SaveCancelled),
            (Receipt(ack: false, candidate: false),
                EuroOfficeNativeSaveAsLifecycle.State.SaveNotAcknowledged),
            (Receipt(candidate: false),
                EuroOfficeNativeSaveAsLifecycle.State.CandidateMissing)
        };
        foreach (var (receipt, expected) in cases)
        {
            var result = EuroOfficeNativeSaveAsLifecycle.Classify(
                receipt, candidateValidated: false);
            Assert.AreEqual(expected, result.Status);
            Assert.IsFalse(result.SaveCompleted);
        }
    }

    [TestMethod]
    public void RejectsConflictDuplicateDialogAndTimeoutBeforeCandidateStatus()
    {
        Assert.AreEqual(EuroOfficeNativeSaveAsLifecycle.State.TargetConflict,
            EuroOfficeNativeSaveAsLifecycle.Classify(
                Receipt(), candidateValidated: true, targetConflict: true).Status);
        Assert.AreEqual(EuroOfficeNativeSaveAsLifecycle.State.DuplicateDialog,
            EuroOfficeNativeSaveAsLifecycle.Classify(
                Receipt(), candidateValidated: true, duplicateDialog: true).Status);
        Assert.AreEqual(EuroOfficeNativeSaveAsLifecycle.State.Timeout,
            EuroOfficeNativeSaveAsLifecycle.Classify(
                Receipt(), candidateValidated: true, timedOut: true).Status);
        Assert.AreEqual(EuroOfficeNativeSaveAsLifecycle.State.CandidateInvalid,
            EuroOfficeNativeSaveAsLifecycle.Classify(
                Receipt(), candidateValidated: false).Status);
    }

    [TestMethod]
    public void RejectsContradictoryOrForeignReceiptBeforeReview()
    {
        var forged = Receipt(request: false);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeSaveAsLifecycle.Classify(
                forged, candidateValidated: true));
        var differentSource = Receipt() with
        {
            SourceAfterSha256 = new string('c', 64)
        };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeSaveAsLifecycle.Classify(
                differentSource, candidateValidated: true));
    }
}
