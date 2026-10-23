using GetDocument.Revisions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Revisions.Tests;

[TestClass]
public sealed class RevisionJournalTests
{
    [TestMethod]
    public void RegistersExistingRevisionAndReloadsJournalAcrossInstances()
    {
        using var temp = TestEnvironment.Create();
        var journal = new FileRevisionJournal(temp.Metadata);
        var record = journal.RecordRevision(temp.Original, temp.Copy, "Редакция №2");
        var loaded = new FileRevisionJournal(temp.Metadata).Read(temp.Original);
        Assert.AreEqual(1, loaded.Revisions.Count);
        Assert.AreEqual(record.Id, loaded.Revisions[0].Id);
        Assert.AreEqual("Редакция №2", loaded.Revisions[0].Label);
        Assert.AreEqual(64, record.Sha256.Length);
        Assert.IsTrue(journal.Verify(record));
        Assert.AreEqual(0, loaded.Comments.Count);
    }

    [TestMethod]
    public void SourceAndRevisionRemainByteIdenticalAfterRegistration()
    {
        using var temp = TestEnvironment.Create();
        byte[] beforeOriginal = File.ReadAllBytes(temp.Original);
        byte[] beforeCopy = File.ReadAllBytes(temp.Copy);
        var journal = new FileRevisionJournal(temp.Metadata);
        journal.RecordRevision(temp.Original, temp.Copy, "Редакция");
        CollectionAssert.AreEqual(beforeOriginal, File.ReadAllBytes(temp.Original));
        CollectionAssert.AreEqual(beforeCopy, File.ReadAllBytes(temp.Copy));
    }

    [TestMethod]
    public void CommentsAreSeparateAddOnlyEvents()
    {
        using var temp = TestEnvironment.Create();
        var journal = new FileRevisionJournal(temp.Metadata);
        var version = journal.RecordRevision(temp.Original, temp.Copy, "v2");
        journal.AddComment(temp.Original, version.Id, "Пользователь (не подтверждён)", "Проверить пункт 2");
        journal.AddComment(temp.Original, Guid.Empty, "Автор", "Замечание к документу");
        var result = journal.Read(temp.Original);
        Assert.AreEqual(2, result.Comments.Count);
        Assert.AreEqual(version.Id, result.Comments[0].RevisionId);
        Assert.AreEqual(Guid.Empty, result.Comments[1].RevisionId);
        Assert.AreEqual("Проверить пункт 2", result.Comments[0].Body);
    }

    [TestMethod]
    public void DetectsExternalMutationWithoutChangingTheSource()
    {
        using var temp = TestEnvironment.Create();
        var journal = new FileRevisionJournal(temp.Metadata);
        var recorded = journal.RecordRevision(temp.Original, temp.Copy, "v2");
        File.AppendAllText(temp.Copy, " изменено");
        Assert.IsFalse(journal.Verify(recorded));
        Assert.IsTrue(File.ReadAllText(temp.Copy).Contains("изменено", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RejectsSamePathMissingFilesAndExistingSourceAsNewRevision()
    {
        using var temp = TestEnvironment.Create();
        var journal = new FileRevisionJournal(temp.Metadata);
        Assert.ThrowsExactly<IOException>(() => journal.RecordRevision(temp.Original, temp.Original, "same"));
        Assert.ThrowsExactly<FileNotFoundException>(() => journal.RecordRevision(
            temp.Original, Path.Combine(temp.Root, "missing.docx"), "missing"));
        Assert.AreEqual(0, journal.Read(temp.Original).Revisions.Count);
    }

    [TestMethod]
    public void CannotAttachCommentToRevisionOfAnotherDocument()
    {
        using var temp = TestEnvironment.Create();
        var journal = new FileRevisionJournal(temp.Metadata);
        var revision = journal.RecordRevision(temp.Original, temp.Copy, "v2");
        string second = Path.Combine(temp.Root, "other.txt");
        File.WriteAllText(second, "other");
        Assert.ThrowsExactly<ArgumentException>(() =>
            journal.AddComment(second, revision.Id, "Автор", "Несвязанный комментарий"));
    }

    [TestMethod]
    public void RejectsUnsafeOrOversizedCommentFields()
    {
        using var temp = TestEnvironment.Create();
        var journal = new FileRevisionJournal(temp.Metadata);
        Assert.ThrowsExactly<ArgumentException>(() =>
            journal.AddComment(temp.Original, Guid.Empty, "Имя\u202e", "Обычное замечание"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            journal.AddComment(temp.Original, Guid.Empty, "Имя", "Замечание\u0001"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            journal.AddComment(temp.Original, Guid.Empty, "Имя", new string('a', 3000)));
        Assert.AreEqual(0, journal.Read(temp.Original).Comments.Count);
    }

    [TestMethod]
    public void MissingMetadataRootDoesNotCreateFilesSilently()
    {
        using var temp = TestEnvironment.Create();
        string absent = Path.Combine(temp.Root, "not-configured");
        Assert.ThrowsExactly<DirectoryNotFoundException>(() => new FileRevisionJournal(absent));
        Assert.IsFalse(Directory.Exists(absent));
    }

    [TestMethod]
    public void DetectsCorruptJournalEntryRatherThanHidingIt()
    {
        using var temp = TestEnvironment.Create();
        var journal = new FileRevisionJournal(temp.Metadata);
        var rev = journal.RecordRevision(temp.Original, temp.Copy, "v2");
        string path = Directory.EnumerateFiles(temp.Metadata, "*.json").Single();
        File.WriteAllText(path, "{ broken");
        Assert.ThrowsExactly<InvalidDataException>(() => journal.Read(temp.Original));
        Assert.IsTrue(journal.Verify(rev)); // hashing the file is independent of journal bytes
    }

    private sealed class TestEnvironment : IDisposable
    {
        public string Root { get; }
        public string Metadata { get; }
        public string Original { get; }
        public string Copy { get; }

        private TestEnvironment(string root)
        {
            Root = root;
            Directory.CreateDirectory(root);
            Metadata = Path.Combine(root, "metadata");
            Directory.CreateDirectory(Metadata);
            Original = Path.Combine(root, "source.txt");
            Copy = Path.Combine(root, "revision.txt");
            File.WriteAllText(Original, "исходник");
            File.WriteAllText(Copy, "редакция");
        }

        public static TestEnvironment Create() =>
            new(Path.Combine(Path.GetTempPath(), "gd08-" + Guid.NewGuid().ToString("N")));

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
