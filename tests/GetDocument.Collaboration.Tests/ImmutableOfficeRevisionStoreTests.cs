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
    public async Task RejectsEmptyPayload()
    {
        using var dir = new TempDirectory();
        var store = new ImmutableOfficeRevisionStore(dir.PathValue);
        await using var empty = Bytes("");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            store.InitializeAsync("doc-6", ".docx", empty));
    }
}
