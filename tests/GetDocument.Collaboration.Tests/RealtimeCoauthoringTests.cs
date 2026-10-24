using System.Text.Json;
using GetDocument.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

[TestClass]
public sealed class RealtimeCoauthoringTests
{
    private static readonly byte[] Secret = Enumerable.Repeat((byte)42, 32).ToArray();
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
    private static WopiDocument File(string user = "employee-a", bool edit = true, bool worm = false) =>
        new("document-42", "base-r1", "Совместная работа.docx", "get", user, user, 1540, edit, worm);

    [TestMethod]
    public void DifferentAuthorizedUsersJoinSameRoomButHaveDifferentSignedTokens()
    {
        var alice = RealtimeCoauthoring.Issue(File("alice"), Secret, Now, TimeSpan.FromMinutes(10),
            editingGatewayReady: true);
        var bob = RealtimeCoauthoring.Issue(File("bob"), Secret, Now, TimeSpan.FromMinutes(10),
            editingGatewayReady: true);
        Assert.AreEqual(alice.DocumentKey, bob.DocumentKey);
        Assert.AreNotEqual(alice.AccessToken, bob.AccessToken);
        Assert.IsTrue(alice.CanEdit);
        Assert.IsTrue(bob.CanEdit);
        Assert.AreEqual("alice", RealtimeCoauthoring.Authorize(alice.AccessToken,
            Secret, File("alice"), Now.AddMinutes(1)).UserId);
        Assert.AreEqual("bob", RealtimeCoauthoring.Authorize(bob.AccessToken,
            Secret, File("bob"), Now.AddMinutes(1)).UserId);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            RealtimeCoauthoring.Authorize(alice.AccessToken, Secret, File("bob"), Now));
    }

    [TestMethod]
    public void NewRevisionChangesRoomButUserDoesNot()
    {
        var first = RealtimeCoauthoring.Issue(File(), Secret, Now,
            TimeSpan.FromMinutes(5), editingGatewayReady: true);
        var second = RealtimeCoauthoring.Issue(File() with { RevisionId = "base-r2" },
            Secret, Now, TimeSpan.FromMinutes(5), editingGatewayReady: true);
        Assert.AreNotEqual(first.DocumentKey, second.DocumentKey);
    }

    [TestMethod]
    public void WormOriginalAndUnreadyGatewayNeverEnableEditing()
    {
        var worm = RealtimeCoauthoring.Issue(File(worm: true), Secret, Now,
            TimeSpan.FromMinutes(5), editingGatewayReady: true);
        var unavailable = RealtimeCoauthoring.Issue(File(), Secret, Now,
            TimeSpan.FromMinutes(5), editingGatewayReady: false);
        Assert.IsFalse(worm.CanEdit);
        Assert.IsFalse(unavailable.CanEdit);
        Assert.IsFalse(RealtimeCoauthoring.Authorize(worm.AccessToken, Secret,
            File(worm: true), Now).CanEdit);
    }

    [TestMethod]
    public void CheckFileInfoIsWriteEnabledOnlyForAuthorizedNonWormEditingSession()
    {
        var doc = File();
        var session = RealtimeCoauthoring.Issue(doc, Secret, Now,
            TimeSpan.FromMinutes(5), editingGatewayReady: true);
        var grant = RealtimeCoauthoring.Authorize(session.AccessToken, Secret, doc, Now);
        using var json = JsonDocument.Parse(RealtimeCoauthoring.CheckFileInfo(doc, grant, Now,
            Now.AddMinutes(-2)));
        var root = json.RootElement;
        Assert.IsTrue(root.GetProperty("UserCanWrite").GetBoolean());
        Assert.IsTrue(root.GetProperty("SupportsUpdate").GetBoolean());
        Assert.IsFalse(root.GetProperty("SupportsLocks").GetBoolean());
        Assert.AreEqual("base-r1", root.GetProperty("Version").GetString());
        Assert.IsTrue(root.TryGetProperty("LastModifiedTime", out _));
    }

    [TestMethod]
    public void ExpiredTokensCannotReadOrWriteEvenWithOldGrant()
    {
        var doc = File();
        var session = RealtimeCoauthoring.Issue(doc, Secret, Now,
            TimeSpan.FromMinutes(5), editingGatewayReady: true);
        var grant = RealtimeCoauthoring.Authorize(session.AccessToken, Secret, doc, Now);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            RealtimeCoauthoring.CheckFileInfo(doc, grant, Now.AddMinutes(6), Now));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            RealtimeCoauthoring.Authorize(session.AccessToken, Secret, doc, Now.AddMinutes(6)));
    }

    [TestMethod]
    public void SavePlanRequiresMatchingTimestampAndNeverRequestsOverwrite()
    {
        var doc = File();
        var session = RealtimeCoauthoring.Issue(doc, Secret, Now,
            TimeSpan.FromMinutes(5), editingGatewayReady: true);
        var grant = RealtimeCoauthoring.Authorize(session.AccessToken, Secret, doc, Now);
        var savedAt = Now.AddSeconds(-20);
        var plan = RealtimeCoauthoring.PlanNewRevision(doc, grant, Now,
            savedAt, savedAt.ToString("O"));
        Assert.AreEqual(doc.DocumentId, plan.DocumentId);
        Assert.AreEqual(doc.RevisionId, plan.ParentRevisionId);
        Assert.IsFalse(plan.OverwriteOriginal);
        Assert.IsTrue(plan.RequiredImmutableRevisionSink);
        Assert.ThrowsExactly<WopiVersionConflictException>(() =>
            RealtimeCoauthoring.PlanNewRevision(doc, grant, Now,
                savedAt.AddSeconds(1), savedAt.ToString("O")));
        Assert.ThrowsExactly<WopiVersionConflictException>(() =>
            RealtimeCoauthoring.PlanNewRevision(doc, grant, Now, savedAt, null));
    }

    [TestMethod]
    public void ViewerAndWormCannotSave()
    {
        var original = File(worm: true);
        var session = RealtimeCoauthoring.Issue(original, Secret, Now,
            TimeSpan.FromMinutes(5), editingGatewayReady: true);
        var grant = RealtimeCoauthoring.Authorize(session.AccessToken, Secret, original, Now);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            RealtimeCoauthoring.PlanNewRevision(original, grant, Now, Now, Now.ToString("O")));
    }

    [TestMethod]
    public void LongShiftSessionIsValidForTenHoursNotFifteenMinutes()
    {
        var document = File();
        var session = RealtimeCoauthoring.Issue(document, Secret, Now,
            TimeSpan.FromHours(10), editingGatewayReady: true);
        Assert.AreEqual(Now.AddHours(10).ToUnixTimeMilliseconds(), session.AccessTokenTtl);
        var afterSixHours = RealtimeCoauthoring.Authorize(
            session.AccessToken, Secret, document, Now.AddHours(6));
        Assert.IsTrue(afterSixHours.CanEdit);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            RealtimeCoauthoring.Authorize(
                session.AccessToken, Secret, document, Now.AddHours(10)));
    }

    [TestMethod]
    public void InvalidLifetimeOrChangedPermissionsRejectAccess()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            RealtimeCoauthoring.Issue(File(), Secret, Now,
                TimeSpan.FromHours(11), editingGatewayReady: true));
        var doc = File();
        var session = RealtimeCoauthoring.Issue(doc, Secret, Now,
            TimeSpan.FromMinutes(5), editingGatewayReady: true);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            RealtimeCoauthoring.Authorize(session.AccessToken, Secret,
                doc with { CanEdit = false }, Now));
    }
}
