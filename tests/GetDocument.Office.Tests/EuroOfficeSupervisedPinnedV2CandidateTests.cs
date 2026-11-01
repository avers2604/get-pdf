using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeSupervisedPinnedV2CandidateTests
{
    private static readonly Guid Session = Guid.Parse("10203040-5060-7080-90a0-b0c0d0e0f010");
    private static readonly Guid Tab = Guid.Parse("f010e0d0-c0b0-a090-8070-605040302010");
    private const string Fixture = "gd31j6_writer.docx";
    private const string BinarySha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string CoreSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string QtSha = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string OriginalSha = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string SavedSha = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    private static string Valid()
    {
        var nested = new
        {
            schema = "getdoc-native-pinned-v2-ooxml-joined-lab/1",
            sessionId = Session.ToString("N"), tabId = Tab.ToString("N"),
            fixtureName = Fixture,
            failureCode = "candidate_untrusted",
            nativeEventOrderSelfReported = true,
            outputSha256 = SavedSha,
            sourceSha256 = OriginalSha,
            independentOoxmlCorrelated = true,
            nativeSaveProven = false, documentVerified = false,
            saveVerified = false, approvedForProduction = false,
            laboratoryOnly = true
        };
        return JsonSerializer.Serialize(new
        {
            schema = "getdoc-native-pinned-v2-supervised-ooxml-lab/1",
            sessionId = Session.ToString("N"), tabId = Tab.ToString("N"),
            fixtureName = Fixture,
            nativeBinarySha256 = BinarySha,
            runtimeLibrarySha256 = new
            {
                ascdocumentscore = CoreSha,
                qtascdocumentscore = QtSha
            },
            nativeProcessExitZero = true,
            runtimeLibraryPinsMatched = true,
            stdoutSingleLineBounded = true,
            joinedCandidate = nested,
            nativeSaveProven = false, documentVerified = false,
            saveVerified = false, approvedForProduction = false,
            laboratoryOnly = true
        });
    }

    [TestMethod]
    public void ValidSyntheticCandidateStillHasNoPublicationOrSaveRights()
    {
        var result = EuroOfficeSupervisedPinnedV2Candidate.Parse(
            Valid(), Session, Tab, Fixture, BinarySha, CoreSha, QtSha);
        Assert.AreEqual(SavedSha, result.OutputSha256);
        Assert.AreEqual(OriginalSha, result.SourceSha256);
        Assert.AreEqual(BinarySha, result.NativeBinarySha256);
        Assert.IsTrue(result.IndependentOoxmlCorrelated);
        Assert.IsFalse(result.NativeSaveProven);
        Assert.IsFalse(result.DocumentVerified);
        Assert.IsFalse(result.SaveVerified);
        Assert.IsFalse(result.ApprovedForProduction);
    }

    [TestMethod]
    public void WrongSessionUnknownLibrariesDuplicateKeysAndForgedFlagsFailClosed()
    {
        string normal = Valid();
        foreach (string bad in new[]
        {
            normal.Replace(BinarySha, "z" + BinarySha[1..]),
            normal.Replace(CoreSha, "f" + CoreSha[1..]),
            normal.Replace(QtSha, "f" + QtSha[1..]),
            normal.Replace("\"nativeProcessExitZero\":true", "\"nativeProcessExitZero\":false"),
            normal.Replace("\"runtimeLibraryPinsMatched\":true", "\"runtimeLibraryPinsMatched\":false"),
            normal.Replace("\"stdoutSingleLineBounded\":true", "\"stdoutSingleLineBounded\":false"),
            normal.Replace("\"nativeSaveProven\":false", "\"nativeSaveProven\":true"),
            normal.Replace("\"approvedForProduction\":false", "\"approvedForProduction\":true"),
            normal.Replace("\"independentOoxmlCorrelated\":true", "\"independentOoxmlCorrelated\":false"),
            normal.Replace("\"nativeEventOrderSelfReported\":true", "\"nativeEventOrderSelfReported\":false"),
            normal.Replace("\"failureCode\":\"candidate_untrusted\"", "\"failureCode\":\"timeout\""),
            normal.Replace("\"schema\":", "\"schema\":\"extra\",\"schema\":"),
            normal.Replace("\"qtascdocumentscore\":", "\"otherlib\":\"fake\",\"qtascdocumentscore\":"),
            normal.Replace("\"joinedCandidate\":{", "\"joinedCandidate\":null,\"junk\":{"),
            normal.Replace("\"runtimeLibrarySha256\":{", "\"runtimeLibrarySha256\":{\"ascdocumentscore\":\"bad\","),
            normal.Replace("\"sourceSha256\":\"" + OriginalSha, "\"sourceSha256\":\"" + SavedSha),
            normal.Replace("\"sessionId\":\"" + Session.ToString("N"),
                           "\"sessionId\":\"" + Tab.ToString("N")),
            "{}"
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                EuroOfficeSupervisedPinnedV2Candidate.Parse(
                    bad, Session, Tab, Fixture, BinarySha, CoreSha, QtSha));

        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSupervisedPinnedV2Candidate.Parse(
                normal, Guid.NewGuid(), Tab, Fixture, BinarySha, CoreSha, QtSha));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSupervisedPinnedV2Candidate.Parse(
                normal, Session, Tab, "customer.docx", BinarySha, CoreSha, QtSha));
    }
}
