using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeSaveAsProtocolDiagnosticTests
{
    private static readonly Guid Session = Guid.Parse("10203040-5060-7080-90a0-b0c0d0e0f010");
    private static readonly Guid Tab = Guid.Parse("f010e0d0-c0b0-a090-8070-605040302010");
    private const string Fixture = "gd31j6_writer.docx";
    private const string Output = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static string Receipt(bool guarded = false, bool candidate = true) =>
        JsonSerializer.Serialize(new
        {
            schema = "getdoc-native-saveas-protocol-diagnostic-lab/1",
            sourceProtocol = guarded ? "gd31j28-guarded/2" : "gd31j26-pinned/1",
            sessionId = Session.ToString("N"),
            tabId = Tab.ToString("N"),
            fixtureName = Fixture,
            diagnosticState = candidate ? "synthetic_candidate_untrusted" : "unconfirmed",
            reasonCode = candidate ? "candidate_untrusted" :
                guarded ? "duplicate_dialog" : "unspecified_failure",
            outputSha256 = candidate ? Output : "",
            nativeEventOrderSelfReported = guarded && candidate,
            nativeSaveProven = false,
            documentVerified = false,
            saveVerified = false,
            approvedForProduction = false,
            laboratoryOnly = true
        });

    [TestMethod]
    public void BothDiagnosticSourcesNeverGrantSaveOrPublication()
    {
        foreach (var guarded in new[] { false, true })
        {
            var candidate = EuroOfficeSaveAsProtocolDiagnostic.Parse(
                Receipt(guarded), Session, Tab, Fixture);
            Assert.AreEqual(Output, candidate.OutputSha256);
            Assert.AreEqual(guarded, candidate.NativeEventOrderSelfReported);
            Assert.IsFalse(candidate.NativeSaveProven);
            Assert.IsFalse(candidate.DocumentVerified);
            Assert.IsFalse(candidate.SaveVerified);
            Assert.IsFalse(candidate.ApprovedForProduction);

            var failure = EuroOfficeSaveAsProtocolDiagnostic.Parse(
                Receipt(guarded, candidate: false), Session, Tab, Fixture);
            Assert.AreEqual("", failure.OutputSha256);
            Assert.IsFalse(failure.NativeEventOrderSelfReported);
        }
    }

    [TestMethod]
    public void ForgedPrivilegesMixedLineagesAndAmbiguousStatesAreRejected()
    {
        string normal = Receipt();
        string guarded = Receipt(guarded: true);
        foreach (string bad in new[]
        {
            normal.Replace("\"approvedForProduction\":false", "\"approvedForProduction\":true"),
            normal.Replace("\"nativeSaveProven\":false", "\"nativeSaveProven\":true"),
            normal.Replace("\"saveVerified\":false", "\"saveVerified\":true"),
            normal.Replace("\"nativeEventOrderSelfReported\":false",
                           "\"nativeEventOrderSelfReported\":true"),
            normal.Replace("\"sourceProtocol\":\"gd31j26-pinned/1\"",
                           "\"sourceProtocol\":\"gd31j28-guarded/2\""),
            normal.Replace("\"reasonCode\":\"candidate_untrusted\"",
                           "\"reasonCode\":\"duplicate_dialog\""),
            normal.Replace("\"schema\":", "\"schema\":\"duplicated\",\"schema\":"),
            normal.Replace("\"fixtureName\":", "\"path\":\"corporate.docx\",\"fixtureName\":"),
            guarded.Replace("\"nativeEventOrderSelfReported\":true",
                            "\"nativeEventOrderSelfReported\":false"),
            Receipt(candidate: false).Replace("\"outputSha256\":\"\"",
                                                "\"outputSha256\":\"" + Output + "\""),
            Receipt(guarded: true, candidate: false).Replace(
                "\"reasonCode\":\"duplicate_dialog\"",
                "\"reasonCode\":\"candidate_untrusted\""),
            "{}"
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                EuroOfficeSaveAsProtocolDiagnostic.Parse(bad, Session, Tab, Fixture));

        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSaveAsProtocolDiagnostic.Parse(normal, Guid.NewGuid(), Tab, Fixture));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSaveAsProtocolDiagnostic.Parse(normal, Session, Tab, "unknown.docx"));
    }
}
