using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using GetDocument.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

[TestClass]
public sealed class EuroOfficeCallbackAdmissionTests
{
    private static readonly byte[] SigningKey = SHA256.HashData(
        Encoding.UTF8.GetBytes("native-eurooffice-callback-lab-signing-key"));
    private static readonly Uri Origin = new("https://office.intra.get.test/");
    private const string Key =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [TestMethod]
    public async Task WithoutTrustedSessionNoCallbackIsEverProcessed()
    {
        ITrustedEuroOfficeCallbackSessionStore store = new DenyAllEuroOfficeCallbackSessionStore();
        var session = await store.FindAsync(EuroOfficeLaunchId.New(),
            CancellationToken.None);
        Assert.IsNull(session);
    }

    [TestMethod]
    public void ActiveSessionMustUseTrustedDocumentOriginAndShortExpiry()
    {
        var session = new EuroOfficeCallbackSession(Key, Origin,
            DateTimeOffset.UtcNow.AddMinutes(3));
        EuroOfficeCallbackAdmission.RequireCurrent(
            session, Origin, DateTimeOffset.UtcNow);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeCallbackAdmission.RequireCurrent(
                session, new Uri("https://external.invalid/"), DateTimeOffset.UtcNow));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeCallbackAdmission.RequireCurrent(
                session, Origin, DateTimeOffset.UtcNow.AddMinutes(10)));
    }

    [TestMethod]
    public void DenyAllRevisionSinkCannotAcknowledgeAnyNewVersion()
    {
        ITrustedEuroOfficeCallbackRevisionSink sink = new DenyAllEuroOfficeCallbackRevisionSink();
        var candidate = new EuroOfficeSaveCandidate(
            Key, 2, new Uri("https://office.intra.get.test/file.docx"),
            new string('b', 64));
        var outcome = sink.CommitAsync(candidate, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert.IsFalse(outcome.Committed);
    }

    [TestMethod]
    public void CallbackHttpAckNeverClaimsSuccessWithoutRealCommit()
    {
        Assert.AreEqual(1, EuroOfficeCallbackAdmission.ResponseError(
            new EuroOfficeCallbackCommitReceipt(false)));
        Assert.AreEqual(0, EuroOfficeCallbackAdmission.ResponseError(
            new EuroOfficeCallbackCommitReceipt(true)));
    }
}
