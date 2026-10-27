using System.Security.Cryptography;
using System.Text;
using GetDocument.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

[TestClass]
public sealed class EuroOfficeNativeCallbackTests
{
    private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes(
        "eurooffice-outbox-test-secret-32-plus-characters"));
    private static readonly Uri Server = new("https://office.intra.get.test/");
    private const string DocumentKey =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static (string Json, string Jwt) Event(int status,
        string key = DocumentKey,
        string? url = null)
    {
        url ??= "https://office.intra.get.test/cache/files/new.docx";
        var payload = status is 2 or 6
            ? new { key, status, url }
            : new { key, status, url = (string?)null };
        return (System.Text.Json.JsonSerializer.Serialize(payload),
                Hs256Jwt.Sign(payload, Key));
    }

    [DataTestMethod]
    [DataRow(2)]
    [DataRow(6)]
    public void PlansOnlyAuthenticatedSaveEvents(int status)
    {
        var (body, jwt) = Event(status);
        var decision = EuroOfficeNativeCallback.Plan(body, jwt, DocumentKey, Server, Key);
        Assert.IsNotNull(decision);
        Assert.AreEqual(status, decision.Status);
        Assert.AreEqual("office.intra.get.test", decision.DownloadUrl.Host);
        Assert.IsTrue(decision.RequiresApprovedRevisionSink);
        Assert.AreEqual(64, decision.SignedPayloadSha256.Length);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(4)]
    public void IgnoresNonSaveStatusAfterAuthenticating(int status)
    {
        var (body, jwt) = Event(status);
        Assert.IsNull(EuroOfficeNativeCallback.Plan(body, jwt, DocumentKey, Server, Key));
    }

    [DataTestMethod]
    [DataRow(3)]
    [DataRow(7)]
    [DataRow(0)]
    public void RefusesSaveErrorsAndUnrecognizedStates(int status)
    {
        var (body, jwt) = Event(status);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeCallback.Plan(body, jwt, DocumentKey, Server, Key));
    }

    [TestMethod]
    public void SignedJwtCannotBeReusedWithAlteredUnsignedCallbackBody()
    {
        var (_, jwt) = Event(2);
        var (changedBody, _) = Event(6);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeNativeCallback.Plan(changedBody, jwt, DocumentKey, Server, Key));
    }

    [TestMethod]
    public void RejectsWrongDocumentKeyEvenWhenSignedCorrectly()
    {
        var (body, jwt) = Event(2, key: new string('b', 64));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeNativeCallback.Plan(body, jwt, DocumentKey, Server, Key));
    }

    [TestMethod]
    public void RejectsExternalHttpAndCredentialBearingDownloadUrls()
    {
        foreach (string candidate in new [] {
            "https://external.example/new.docx",
            "http://office.intra.get.test/new.docx",
            "https://user:password@office.intra.get.test/new.docx",
            "https://office.intra.get.test/new.docx#fragment"
        })
        {
            var (body, jwt) = Event(2, url: candidate);
            Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
                EuroOfficeNativeCallback.Plan(body, jwt, DocumentKey, Server, Key));
        }
    }

    [TestMethod]
    public void RejectsUnsignedCallbackAndLegacyWrappedJwt()
    {
        var (body, jwt) = Event(2);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeNativeCallback.Plan(body, jwt + "a", DocumentKey, Server, Key));
        string wrappedJwt = Hs256Jwt.Sign(new {
            operation = "callback", payload = new {
                key = DocumentKey, status = 2,
                url = "https://office.intra.get.test/cache/files/new.docx"
            }
        }, Key);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeNativeCallback.Plan(body, wrappedJwt, DocumentKey, Server, Key));
    }

    [TestMethod]
    public void SaveCandidateIsNeverEquivalentToConfirmedGup2Commit()
    {
        var (body, jwt) = Event(2);
        var plan = EuroOfficeNativeCallback.Plan(body, jwt, DocumentKey, Server, Key);
        Assert.IsNotNull(plan);
        Assert.IsTrue(plan.RequiresApprovedRevisionSink);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            EuroOfficeCallbackCommitPolicy.RequireReady(
                revisionSinkAvailable: false, approvedGup2Cas: true,
                offlineStoragePinned: true));
    }

    [TestMethod]
    public void CallbackCommitRequiresAllCorporateGuards()
    {
        foreach (var combination in new[] {
            (false, true, true), (true, false, true), (true, true, false)
        })
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                EuroOfficeCallbackCommitPolicy.RequireReady(
                    combination.Item1, combination.Item2, combination.Item3));
        EuroOfficeCallbackCommitPolicy.RequireReady(true, true, true);
    }
}
