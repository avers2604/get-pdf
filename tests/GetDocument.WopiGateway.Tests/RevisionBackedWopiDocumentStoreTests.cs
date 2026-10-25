using System.Text;
using GetDocument.Collaboration;
using GetDocument.WopiGateway;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.WopiGateway.Tests;

[TestClass]
public sealed class RevisionBackedWopiDocumentStoreTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Secret = Enumerable.Repeat((byte)77, 32).ToArray();

    private static ApprovedOfficeDocument Approved(bool edit = true, bool worm = false) =>
        new("doc-42", "room-1", "Order.docx", "get",
            "alice", "Employee Alice", edit, worm);

    [TestMethod]
    public async Task EnterprisePolicyResolvesOneAuthorizedImmutableDocument()
    {
        using var temp = new TestFolder();
        var revisions = new ImmutableOfficeRevisionStore(temp.Root);
        await using var bytes = Data("source WORM bytes");
        var first = await revisions.InitializeAsync("doc-42", ".docx", bytes);
        var policy = new FakePolicy(Approved());
        var adapter = new RevisionBackedWopiDocumentStore(revisions, policy, new FixedClock(Now));
        var result = await adapter.ResolveForSessionAsync("doc-42", "room-1", "alice",
            CancellationToken.None);
        Assert.IsNotNull(result);
        Assert.AreEqual(first.RevisionId, result.CurrentRevision.RevisionId);
        Assert.AreEqual("alice", result.Document.UserId);
        Assert.AreEqual("room-1", result.Document.RevisionId);
        await using var reader = await result.OpenReadAsync(CancellationToken.None);
        using var sr = new StreamReader(reader, Encoding.UTF8);
        Assert.AreEqual("source WORM bytes", await sr.ReadToEndAsync());
    }

    [TestMethod]
    public async Task DefaultMissingPermissionsNeverReadStorage()
    {
        using var temp = new TestFolder();
        var revisions = new ImmutableOfficeRevisionStore(temp.Root);
        var policy = new FakePolicy(null);
        var adapter = new RevisionBackedWopiDocumentStore(revisions, policy, new FixedClock(Now));
        Assert.IsNull(await adapter.ResolveForSessionAsync(
            "doc-42", "room-1", "alice", CancellationToken.None));
        Assert.AreEqual(1, policy.QueryCount);
    }

    [TestMethod]
    public async Task EnterpriseRevocationBeforeCommitDeniesWrite()
    {
        using var temp = new TestFolder();
        var revisions = new ImmutableOfficeRevisionStore(temp.Root);
        await using var initial = Data("unchanged");
        var first = await revisions.InitializeAsync("doc-42", ".docx", initial);
        var policy = new FakePolicy(Approved());
        var adapter = new RevisionBackedWopiDocumentStore(revisions, policy, new FixedClock(Now));
        var allowed = await adapter.ResolveForSessionAsync(
            "doc-42", "room-1", "alice", CancellationToken.None);
        Assert.IsNotNull(allowed);
        var token = RealtimeCoauthoring.Issue(allowed.Document, Secret, Now,
            TimeSpan.FromMinutes(5), true);
        var grant = RealtimeCoauthoring.Authorize(token.AccessToken, Secret, allowed.Document, Now);
        policy.Current = Approved(edit: false);
        await using var edited = Data("unauthorized changes");
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
            adapter.CommitNewRevisionAsync(allowed, grant, first.LastModifiedUtc.ToString("O"),
                edited, CancellationToken.None).AsTask());
        Assert.AreEqual(first.RevisionId, revisions.ReadCurrent("doc-42").RevisionId);
        Assert.AreEqual("unchanged", File.ReadAllText(first.FilePath));
    }

    [TestMethod]
    public async Task ApprovedSaveCreatesNewImmutableRevisionAndRejectsSecondStaleSave()
    {
        using var temp = new TestFolder();
        var revisions = new ImmutableOfficeRevisionStore(temp.Root);
        await using var original = Data("first version");
        var first = await revisions.InitializeAsync("doc-42", ".docx", original);
        var policy = new FakePolicy(Approved());
        var adapter = new RevisionBackedWopiDocumentStore(revisions, policy, new FixedClock(Now));
        var allowed = await adapter.ResolveForSessionAsync(
            "doc-42", "room-1", "alice", CancellationToken.None);
        Assert.IsNotNull(allowed);
        var token = RealtimeCoauthoring.Issue(allowed.Document, Secret, Now,
            TimeSpan.FromMinutes(5), true);
        var grant = RealtimeCoauthoring.Authorize(token.AccessToken, Secret, allowed.Document, Now);
        await using var edited = Data("alice & bob final");
        var next = await adapter.CommitNewRevisionAsync(allowed, grant,
            first.LastModifiedUtc.ToString("O"), edited, CancellationToken.None);
        Assert.AreEqual("first version", File.ReadAllText(first.FilePath));
        Assert.AreEqual("alice & bob final", File.ReadAllText(next.FilePath));
        Assert.AreNotEqual(first.RevisionId, next.RevisionId);
        await using var second = Data("old contents");
        await Assert.ThrowsExactlyAsync<WopiVersionConflictException>(() =>
            adapter.CommitNewRevisionAsync(allowed, grant,
                first.LastModifiedUtc.ToString("O"), second, CancellationToken.None).AsTask());
        Assert.AreEqual(next.RevisionId, revisions.ReadCurrent("doc-42").RevisionId);
    }

    [TestMethod]
    public async Task PolicyCannotRedirectLookupToOtherDocumentOrOtherEmployee()
    {
        using var temp = new TestFolder();
        var revisions = new ImmutableOfficeRevisionStore(temp.Root);
        await using var initial = Data("content");
        await revisions.InitializeAsync("doc-42", ".docx", initial);
        var policy = new FakePolicy(Approved() with { DocumentId = "another-document" });
        var adapter = new RevisionBackedWopiDocumentStore(revisions, policy, new FixedClock(Now));
        Assert.IsNull(await adapter.ResolveForSessionAsync(
            "doc-42", "room-1", "alice", CancellationToken.None));
        policy.Current = Approved() with { UserId = "bob" };
        Assert.IsNull(await adapter.ResolveForSessionAsync(
            "doc-42", "room-1", "alice", CancellationToken.None));
    }

    private static MemoryStream Data(string text) => new(Encoding.UTF8.GetBytes(text));

    private sealed class FixedClock(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
    }

    private sealed class FakePolicy(ApprovedOfficeDocument? current) : IEnterpriseDocumentPolicy
    {
        public ApprovedOfficeDocument? Current { get; set; } = current;
        public int QueryCount { get; private set; }

        public ValueTask<ApprovedOfficeDocument?> GetPermissionAsync(
            string doc, string session, string user, CancellationToken ct)
        {
            QueryCount++;
            return ValueTask.FromResult(Current);
        }
    }

    private sealed class TestFolder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "gd09f-adapter-" + Guid.NewGuid().ToString("N"));

        public TestFolder() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
