using System.Text;
using GetDocument.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

[TestClass]
public sealed class ImmutableOfficeRevisionStoreTests
{
    private sealed class TempDirectory : IDisposable
    {
        public string PathValue { get; } = Path.Combine(Path.GetTempPath(),
            "get-document-live-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(PathValue);
        public void Dispose() => Directory.Delete(PathValue, recursive: true);
    }

    private static MemoryStream Bytes(string text) =>
        new(Encoding.UTF8.GetBytes(text));

    [TestMethod]
    public async Task CreatesImmutableSnapshotThenNewRevisionWithoutOverwritingOriginal()
    {
        using var dir = new TempDirectory();
        string wormSource = Path.Combine(dir.PathValue, "WORM.docx");
        File.WriteAllText(wormSource, "WORM ORIG");
        string storeDir = Path.Combine(dir.PathValue, "versions");
        Directory.CreateDirectory(storeDir);
        var store = new ImmutableOfficeRevisionStore(storeDir);
        await using var input = File.OpenRead(wormSource);
        var first = await store.InitializeAsync("doc-1", ".docx", input);
        await using var edited = Bytes("user A + user B");
        var next = await store.AppendAsync("doc-1", first.RevisionId, edited);

        Assert.AreNotEqual(first.RevisionId, next.RevisionId);
        Assert.AreNotEqual(first.FilePath, next.FilePath);
        Assert.AreEqual("WORM ORIG", File.ReadAllText(wormSource));
        Assert.AreEqual("WORM ORIG", File.ReadAllText(first.FilePath));
        Assert.AreEqual("user A + user B", File.ReadAllText(next.FilePath));
        Assert.AreEqual(next.RevisionId,
            new ImmutableOfficeRevisionStore(storeDir).ReadCurrent("doc-1").RevisionId);
    }

    [TestMethod]
    public async Task StaleCollaboratorCannotOverwriteMoreRecentRevision()
    {
        using var dir = new TempDirectory();
        var store = new ImmutableOfficeRevisionStore(dir.PathValue);
        await using var initial = Bytes("initial");
        var first = await store.InitializeAsync("doc-2", ".xlsx", initial);
        await using var firstSave = Bytes("alice");
        var alice = await store.AppendAsync("doc-2", first.RevisionId, firstSave);
        await using var stale = Bytes("bob stale");
        await Assert.ThrowsExactlyAsync<OfficeRevisionConflictException>(() =>
            store.AppendAsync("doc-2", first.RevisionId, stale));
        Assert.AreEqual(alice.RevisionId, store.ReadCurrent("doc-2").RevisionId);
        Assert.AreEqual("alice", File.ReadAllText(alice.FilePath));
    }

    [TestMethod]
    public async Task StorePersistsRevisionHashesAndRejectsTampering()
    {
        using var dir = new TempDirectory();
        var store = new ImmutableOfficeRevisionStore(dir.PathValue);
        await using var initial = Bytes("content");
        var first = await store.InitializeAsync("doc-3", ".pptx", initial);
        Assert.AreEqual(64, first.Sha256.Length);
        File.AppendAllText(first.FilePath, "tampered");
        Assert.ThrowsExactly<InvalidDataException>(() => store.ReadCurrent("doc-3"));
    }

    [TestMethod]
    public async Task RejectsMissingStorageDirectoryAndUnsupportedExtension()
    {
        using var dir = new TempDirectory();
        Assert.ThrowsExactly<DirectoryNotFoundException>(() =>
            new ImmutableOfficeRevisionStore(Path.Combine(dir.PathValue, "missing")));
        var store = new ImmutableOfficeRevisionStore(dir.PathValue);
        await using var stream = Bytes("malicious");
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            store.InitializeAsync("doc-4", ".exe", stream));
    }

    [TestMethod]
    public async Task RepeatedInitializationNeverReplacesExistingSnapshot()
    {
        using var dir = new TempDirectory();
        var store = new ImmutableOfficeRevisionStore(dir.PathValue);
        await using var firstData = Bytes("first");
        var first = await store.InitializeAsync("doc-5", ".docx", firstData);
        await using var secondData = Bytes("second");
        var second = await store.InitializeAsync("doc-5", ".docx", secondData);
        Assert.AreEqual(first.RevisionId, second.RevisionId);
        Assert.AreEqual("first", File.ReadAllText(second.FilePath));
    }

    [TestMethod]
    public async Task ConcurrentSavesOfSameRevisionPublishExactlyOneWinner()
    {
        using var dir = new TempDirectory();
        var store = new ImmutableOfficeRevisionStore(dir.PathValue);
        await using var starting = Bytes("shared version");
        var original = await store.InitializeAsync("shared-doc", ".docx", starting);

        async Task<StoredOfficeRevision?> TrySaveAsync(int number)
        {
            await using var bytes = Bytes("save-" + number);
            try { return await store.AppendAsync("shared-doc", original.RevisionId, bytes); }
            catch (OfficeRevisionConflictException) { return null; }
        }

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(n => Task.Run(() => TrySaveAsync(n))));
        Assert.AreEqual(1, results.Count(r => r is not null));
        var winner = results.Single(r => r is not null)!;
        Assert.AreEqual(winner.RevisionId, store.ReadCurrent("shared-doc").RevisionId);
        Assert.AreEqual("shared version", File.ReadAllText(original.FilePath));
    }

    [TestMethod]
    public async Task BriefExistingFilesystemLockWaitsBeforeRecheckingRevision()
    {
        using var dir = new TempDirectory();
        var store = new ImmutableOfficeRevisionStore(dir.PathValue);
        await using var starting = Bytes("beginning");
        var initial = await store.InitializeAsync("locked-doc", ".xlsx", starting);
        string key = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes("locked-doc"))).ToLowerInvariant();
        string lockPath = Path.Combine(dir.PathValue, key + ".lock");
        await using var edit = Bytes("new edit");

        Task<StoredOfficeRevision> saveTask;
        using (var exclusive = new FileStream(lockPath, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None))
        {
            saveTask = store.AppendAsync("locked-doc", initial.RevisionId, edit);
            await Task.Delay(60);
            Assert.IsFalse(saveTask.IsCompleted, "Expected a bounded retry, not immediate IOException.");
        }
        var saved = await saveTask;
        Assert.AreNotEqual(initial.RevisionId, saved.RevisionId);
        Assert.AreEqual("beginning", File.ReadAllText(initial.FilePath));
    }

    [TestMethod]
    public async Task RejectsEmptyPayload()
    {
        using var dir = new TempDirectory();
        var store = new ImmutableOfficeRevisionStore(dir.PathValue);
        await using var empty = Bytes("");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            store.InitializeAsync("doc-6", ".docx", empty));
    }
}
