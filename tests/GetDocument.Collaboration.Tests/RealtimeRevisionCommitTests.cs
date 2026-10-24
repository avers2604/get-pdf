using System.Text;
using System.Text.Json;
using GetDocument.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

[TestClass]
public sealed class RealtimeRevisionCommitTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Secret = Enumerable.Repeat((byte)56, 32).ToArray();
    private static MemoryStream Data(string input) => new(Encoding.UTF8.GetBytes(input));
    private static WopiDocument Doc(string user = "alice", bool worm = false) =>
        new("team-document", "session-base-r1", "Work.docx",
            "get", user, user, 100, true, worm);

    private static RealtimeCoauthoringGrant Grant(WopiDocument doc) =>
        RealtimeCoauthoring.Authorize(
            RealtimeCoauthoring.Issue(doc, Secret, Now, TimeSpan.FromMinutes(5), true).AccessToken,
            Secret, doc, Now);

    [TestMethod]
    public async Task TwoEditorsSaveAsNewRevisionsAndStaleSaveCannotOverwriteFirstEditor()
    {
        string folder = Path.Combine(Path.GetTempPath(), "get-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new ImmutableOfficeRevisionStore(folder);
            await using var original = Data("WORM original bytes");
            var initial = await store.InitializeAsync("team-document", ".docx", original);
            var alice = Doc("alice");
            var bob = Doc("bob");
            var aGrant = Grant(alice);
            var bGrant = Grant(bob);
            await using var aliceBytes = Data("Alice edit shared with Bob");
            var firstSave = await RealtimeRevisionCommit.CommitAsync(
                store, alice, aGrant, Now, initial.LastModifiedUtc.ToString("O"), aliceBytes);
            Assert.AreEqual("WORM original bytes", File.ReadAllText(initial.FilePath));
            Assert.AreEqual("Alice edit shared with Bob", File.ReadAllText(firstSave.FilePath));

            await using var bobStaleBytes = Data("Bob based on old version");
            await Assert.ThrowsExactlyAsync<WopiVersionConflictException>(() =>
                RealtimeRevisionCommit.CommitAsync(store, bob, bGrant, Now,
                    initial.LastModifiedUtc.ToString("O"), bobStaleBytes));
            Assert.AreEqual(firstSave.RevisionId, store.ReadCurrent("team-document").RevisionId);

            await using var bobFreshBytes = Data("Alice and Bob merged content");
            var merged = await RealtimeRevisionCommit.CommitAsync(
                store, bob, bGrant, Now, firstSave.LastModifiedUtc.ToString("O"), bobFreshBytes);
            Assert.AreEqual("Alice and Bob merged content", File.ReadAllText(merged.FilePath));
            Assert.AreEqual("WORM original bytes", File.ReadAllText(initial.FilePath));

            using var info = JsonDocument.Parse(RealtimeCoauthoring.CheckFileInfo(
                alice, aGrant, Now, merged));
            Assert.AreEqual(merged.RevisionId,
                info.RootElement.GetProperty("Version").GetString());
            Assert.AreEqual(merged.Length,
                info.RootElement.GetProperty("Size").GetInt64());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public async Task WormDocumentCannotCommitEvenWithExistingRevisionStore()
    {
        string folder = Path.Combine(Path.GetTempPath(), "get-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new ImmutableOfficeRevisionStore(folder);
            await using var initialBytes = Data("immutable");
            var initial = await store.InitializeAsync("team-document", ".docx", initialBytes);
            var worm = Doc(worm: true);
            await using var attempt = Data("forbidden");
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
                RealtimeRevisionCommit.CommitAsync(store, worm, Grant(worm), Now,
                    initial.LastModifiedUtc.ToString("O"), attempt));
            Assert.AreEqual(initial.RevisionId, store.ReadCurrent("team-document").RevisionId);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
