using System.Text.Json;
using GetDocument.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

[TestClass]
public sealed class WopiReadOnlySessionTests
{
    private static readonly byte[] Secret = Enumerable.Repeat((byte)17, 32).ToArray();
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static WopiDocument Document(bool canEdit = true, bool worm = false) => new(
        DocumentId: "get-42", RevisionId: "r2",
        FileName: "Приказ.docx", OwnerId: "organization-get",
        UserId: "employee-1", UserFriendlyName: "Сотрудник",
        FileSize: 1250, CanEdit: canEdit, IsWormOriginal: worm);

    [TestMethod]
    public void IssuesTimeLimitedSessionAndBuildsReadOnlyCheckFileInfo()
    {
        var session = WopiReadOnlySessionService.Issue(Document(), Secret, Now, TimeSpan.FromMinutes(5));
        Assert.AreEqual(Now.AddMinutes(5).ToUnixTimeMilliseconds(), session.AccessTokenTtl);
        var grant = WopiReadOnlySessionService.Authorize(
            session.AccessToken, Secret, Document(), Now.AddMinutes(1));
        using var json = JsonDocument.Parse(WopiReadOnlySessionService.CheckFileInfo(Document(), grant));
        var root = json.RootElement;
        Assert.AreEqual("Приказ.docx", root.GetProperty("BaseFileName").GetString());
        Assert.AreEqual("employee-1", root.GetProperty("UserId").GetString());
        Assert.AreEqual("r2", root.GetProperty("Version").GetString());
        Assert.AreEqual(1250L, root.GetProperty("Size").GetInt64());
        Assert.IsFalse(root.GetProperty("UserCanWrite").GetBoolean());
        Assert.IsFalse(root.GetProperty("SupportsUpdate").GetBoolean());
        Assert.IsFalse(root.GetProperty("SupportsLocks").GetBoolean());
        Assert.IsTrue(root.GetProperty("ReadOnly").GetBoolean());
    }

    [TestMethod]
    public void WormNeverReceivesWritePermissions()
    {
        var item = Document(canEdit: true, worm: true);
        var session = WopiReadOnlySessionService.Issue(item, Secret, Now, TimeSpan.FromMinutes(5));
        var authorized = WopiReadOnlySessionService.Authorize(session.AccessToken, Secret, item, Now);
        using var json = JsonDocument.Parse(WopiReadOnlySessionService.CheckFileInfo(item, authorized));
        Assert.IsFalse(json.RootElement.GetProperty("UserCanWrite").GetBoolean());
    }

    [TestMethod]
    public void ExpiredAndNotYetValidTokensAreRejected()
    {
        var session = WopiReadOnlySessionService.Issue(Document(), Secret, Now, TimeSpan.FromMinutes(5));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            WopiReadOnlySessionService.Authorize(session.AccessToken, Secret, Document(), Now.AddMinutes(6)));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            WopiReadOnlySessionService.Authorize(session.AccessToken, Secret, Document(), Now.AddMinutes(-1)));
    }

    [TestMethod]
    public void TamperingAndCrossDocumentReuseAreRejected()
    {
        var session = WopiReadOnlySessionService.Issue(Document(), Secret, Now, TimeSpan.FromMinutes(5));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            WopiReadOnlySessionService.Authorize(session.AccessToken + "x", Secret, Document(), Now));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            WopiReadOnlySessionService.Authorize(session.AccessToken, Secret,
                Document() with { DocumentId = "another" }, Now));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            WopiReadOnlySessionService.Authorize(session.AccessToken, Secret,
                Document() with { RevisionId = "r3" }, Now));
    }

    [TestMethod]
    public void CannotIssueTokensWithUnsafeLifetimeOrDocumentNames()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            WopiReadOnlySessionService.Issue(Document(), Secret, Now, TimeSpan.FromHours(2)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            WopiReadOnlySessionService.Issue(
                Document() with { FileName = "../private.docx" }, Secret, Now, TimeSpan.FromMinutes(5)));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            WopiReadOnlySessionService.Issue(
                Document() with { FileName = "script.js" }, Secret, Now, TimeSpan.FromMinutes(5)));
    }

    [TestMethod]
    public void CheckFileInfoDoesNotExposeFilesystemPathOrAccessToken()
    {
        var doc = Document();
        var session = WopiReadOnlySessionService.Issue(doc, Secret, Now, TimeSpan.FromMinutes(5));
        var grant = WopiReadOnlySessionService.Authorize(session.AccessToken, Secret, doc, Now);
        string json = WopiReadOnlySessionService.CheckFileInfo(doc, grant);
        Assert.IsFalse(json.Contains(session.AccessToken, StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("C:\\", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void GetFilePlanningRemainsBoundToDocumentAndCannotReadFromDisk()
    {
        var doc = Document();
        var session = WopiReadOnlySessionService.Issue(doc, Secret, Now, TimeSpan.FromMinutes(5));
        var grant = WopiReadOnlySessionService.Authorize(session.AccessToken, Secret, doc, Now);
        var plan = WopiReadOnlySessionService.PlanGetFile(doc, grant);
        Assert.AreEqual("get-42", plan.DocumentId);
        Assert.AreEqual(1250, plan.ExpectedSize);
        Assert.IsTrue(plan.ReadOnly);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            WopiReadOnlySessionService.PlanGetFile(
                doc with { DocumentId = "other-document" }, grant));
    }

    [TestMethod]
    public void NoPutFileCapabilityCanBeAdvertisedUntilServerSinkExists()
    {
        var doc = Document();
        var session = WopiReadOnlySessionService.Issue(doc, Secret, Now, TimeSpan.FromMinutes(5));
        var grant = WopiReadOnlySessionService.Authorize(session.AccessToken, Secret, doc, Now);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            WopiReadOnlySessionService.PlanPutFile(grant));
    }
}
