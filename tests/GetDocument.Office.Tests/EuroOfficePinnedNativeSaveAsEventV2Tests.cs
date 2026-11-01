using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficePinnedNativeSaveAsEventV2Tests
{
    private static readonly Guid Session = Guid.Parse("10203040-5060-7080-90a0-b0c0d0e0f010");
    private static readonly Guid Tab = Guid.Parse("f010e0d0-c0b0-a090-8070-605040302010");
    private const string Fixture = "gd31j6_writer.docx";
    private const string Digest = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    private static string Receipt(bool saved = true, bool published = true, bool cancelled = false,
        bool request = true, bool path = true) => JsonSerializer.Serialize(new
        {
            schema = "getdoc-native-saveas-candidate-lab/1",
            sessionId = Session.ToString("N"), tabId = Tab.ToString("N"),
            fixtureName = Fixture,
            nativeSaveRequestObserved = request, savePathProvided = path,
            nativeOnSaveAfterAnswer = saved, saveCancelledAfterAnswer = cancelled,
            sourceUnchanged = true, publishedToDisposableOutput = published,
            outputSha256 = published ? Digest : "",
            nativeSaveProven = false, documentVerified = false, saveVerified = false,
            pdfVerified = false, printApproved = false, approvedForProduction = false,
            laboratoryOnly = true
        });

    private static string V2(
        string code = "candidate_untrusted", string phase = "published_disposable",
        int dialogCount = 1, int onSaveCount = 1,
        int dialogSequence = 1, int onSaveSequence = 2,
        bool invalidDialog = false, bool unexpectedOnSave = false,
        string? nested = null)
    {
        using JsonDocument parsed = JsonDocument.Parse(nested ?? Receipt());
        return JsonSerializer.Serialize(new
        {
            schema = "getdoc-native-saveas-pinned-event-lab/2",
            sessionId = Session.ToString("N"), tabId = Tab.ToString("N"),
            fixtureName = Fixture, failureCode = code, phase, dialogCount, onSaveCount,
            dialogSequence, onSaveSequence, invalidDialogObserved = invalidDialog,
            unexpectedOnSaveObserved = unexpectedOnSave, receipt = parsed.RootElement
        });
    }

    [TestMethod]
    public void PositiveNativeV2HasOrderedEventsButNeverProductionRights()
    {
        var r = EuroOfficePinnedNativeSaveAsEventV2.Parse(
            V2(), Session, Tab, Fixture);
        Assert.AreEqual(Digest, r.OutputSha256);
        Assert.IsTrue(r.NativeCallbackSequenceSelfReported);
        Assert.AreEqual("candidate_untrusted", r.FailureCode);
        Assert.IsFalse(r.NativeSaveProven);
        Assert.IsFalse(r.DocumentVerified);
        Assert.IsFalse(r.SaveVerified);
        Assert.IsFalse(r.ApprovedForProduction);
    }

    [TestMethod]
    public void DuplicateDialogAndTimeoutAreExplicitUnconfirmedDiagnostics()
    {
        var duplicate = EuroOfficePinnedNativeSaveAsEventV2.Parse(
            V2("duplicate_dialog", "on_save_observed", 2, 1, 1, 2,
               nested: Receipt(request: false, published: false)),
            Session, Tab, Fixture);
        Assert.AreEqual("duplicate_dialog", duplicate.FailureCode);
        Assert.IsFalse(duplicate.NativeCallbackSequenceSelfReported);

        var timeout = EuroOfficePinnedNativeSaveAsEventV2.Parse(
            V2("timeout", "target_assigned", 1, 0, 1, 0,
               nested: Receipt(saved: false, published: false)),
            Session, Tab, Fixture);
        Assert.AreEqual("timeout", timeout.FailureCode);
        Assert.AreEqual("", timeout.OutputSha256);
    }

    [TestMethod]
    public void OldV1AndGuardedV2CannotDowngradeToPinnedV2()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficePinnedNativeSaveAsEventV2.Parse(
                Receipt(), Session, Tab, Fixture));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficePinnedNativeSaveAsEventV2.Parse(
                V2().Replace("getdoc-native-saveas-pinned-event-lab/2",
                    "getdoc-native-saveas-event-lab/2"), Session, Tab, Fixture));
    }

    [TestMethod]
    public void WrongCountsDuplicateKeysCrossSessionAndForgedPrivilegesFail()
    {
        string good = V2();
        foreach (string bad in new[]
        {
            V2(dialogCount: 2), V2(onSaveCount: 2),
            V2(dialogSequence: 2, onSaveSequence: 1),
            V2(invalidDialog: true), V2(unexpectedOnSave: true),
            V2(code: "duplicate_dialog"), V2(phase: "quarantined"),
            V2(nested: Receipt(saved: false)),
            good.Replace("\"schema\":", "\"schema\":\"duplicate\",\"schema\":"),
            good.Replace("\"receipt\":{", "\"receipt\":{\"schema\":\"duplicate\","),
            good.Replace("\"fixtureName\":", "\"extra\":\"corp.docx\",\"fixtureName\":"),
            good.Replace("\"nativeSaveProven\":false", "\"nativeSaveProven\":true"),
            good.Replace("\"approvedForProduction\":false", "\"approvedForProduction\":true"),
            good.Replace("\"dialogCount\":1", "\"dialogCount\":true"),
            good.Replace("\"sessionId\":\"" + Session.ToString("N"),
                "\"sessionId\":\"" + Tab.ToString("N"))
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                EuroOfficePinnedNativeSaveAsEventV2.Parse(bad, Session, Tab, Fixture));
    }
}
