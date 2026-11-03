using System.Security.Cryptography;
using System.Text;
using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27ao: no real editor, no recovery publication and no cleanup.
/// These isolated tests use only names and dummy bytes in temporary directories.
/// </summary>
[TestClass]
public sealed class OfficeRecoveryIncompleteInventoryTests
{
    [TestMethod]
    public void NoRecoveryDirectoryIsEmptyWithoutCreatingIt()
    {
        using var fixture = new Scratch();
        var store = new OfficeRecoveryStore(fixture.RecoveryRoot);
        var result = store.InspectIncomplete(fixture.Source);

        Assert.AreEqual(0, result.Candidates.Count);
        Assert.IsFalse(result.Truncated);
        Assert.IsFalse(Directory.Exists(fixture.RecoveryRoot));
    }

    [TestMethod]
    public void FindsOnlyExactGuidStagedCopyAndTemporaryManifestWithoutChangingBytes()
    {
        using var fixture = new Scratch();
        string folder = fixture.CreateSourceRecoveryFolder();
        string id = Guid.NewGuid().ToString("N");
        string staged = Path.Combine(folder, id + ".part.docx");
        string manifest = Path.Combine(folder, id + ".json.tmp");
        string foreign = Path.Combine(folder, "personnel-original.docx");
        File.WriteAllText(staged, "STAGED_SYNTHETIC");
        File.WriteAllText(manifest, "NOT_A_REAL_MANIFEST");
        File.WriteAllText(foreign, "UNTOUCHED");
        File.WriteAllText(Path.Combine(folder, "not-a-guid.part.docx"), "IGNORE");
        File.WriteAllText(Path.Combine(folder, id + ".part.xlsx"), "OTHER_FORMAT");
        File.WriteAllText(Path.Combine(folder, id + ".json"), "PUBLISHED");

        var result = new OfficeRecoveryStore(fixture.RecoveryRoot).InspectIncomplete(fixture.Source);
        Assert.AreEqual(2, result.Candidates.Count);
        Assert.IsFalse(result.Truncated);
        Assert.IsTrue(result.Candidates.Any(x =>
            x.FileName == id + ".part.docx" &&
            x.Kind == OfficeRecoveryIncompleteKind.StagedOfficeCopy &&
            x.Status == OfficeRecoveryIncompleteStatus.RegularUnverified));
        Assert.IsTrue(result.Candidates.Any(x =>
            x.FileName == id + ".json.tmp" &&
            x.Kind == OfficeRecoveryIncompleteKind.TemporaryManifest &&
            x.Status == OfficeRecoveryIncompleteStatus.RegularUnverified));
        Assert.IsTrue(result.Candidates.All(x => x.LengthBytes is > 0 &&
            x.LastWriteUtc is not null));
        Assert.AreEqual("UNTOUCHED", File.ReadAllText(foreign));
        Assert.AreEqual("STAGED_SYNTHETIC", File.ReadAllText(staged));
        Assert.AreEqual("NOT_A_REAL_MANIFEST", File.ReadAllText(manifest));
        Assert.AreEqual(6, Directory.GetFileSystemEntries(folder).Length);
    }

    [TestMethod]
    public void LinkedTemporaryManifestIsListedAsUntrustedWithoutReadingItsTarget()
    {
        using var fixture = new Scratch();
        string folder = fixture.CreateSourceRecoveryFolder();
        string protectedPath = Path.Combine(fixture.Root, "other-file.txt");
        File.WriteAllText(protectedPath, "KEEP_SENSITIVE_CONTENT");
        string link = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json.tmp");
        File.CreateSymbolicLink(link, protectedPath);

        var result = new OfficeRecoveryStore(fixture.RecoveryRoot).InspectIncomplete(fixture.Source);
        var item = result.Candidates.Single();
        Assert.AreEqual(OfficeRecoveryIncompleteStatus.UntrustedLinkOrNonFile, item.Status);
        Assert.IsNull(item.LengthBytes);
        Assert.IsNull(item.LastWriteUtc);
        Assert.AreEqual("KEEP_SENSITIVE_CONTENT", File.ReadAllText(protectedPath));
        Assert.IsTrue(File.Exists(link));
    }

    [TestMethod]
    public void CandidateDirectoryIsNeverEnteredOrTreatedAsFile()
    {
        using var fixture = new Scratch();
        string folder = fixture.CreateSourceRecoveryFolder();
        string directory = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".part.docx");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "keep.txt"), "KEEP");

        var result = new OfficeRecoveryStore(fixture.RecoveryRoot).InspectIncomplete(fixture.Source);
        var item = result.Candidates.Single();
        Assert.AreEqual(OfficeRecoveryIncompleteStatus.UntrustedLinkOrNonFile, item.Status);
        Assert.IsNull(item.LengthBytes);
        Assert.AreEqual("KEEP", File.ReadAllText(Path.Combine(directory, "keep.txt")));
    }

    [TestMethod]
    public void EntryBudgetIsBoundedAndReportedAsTruncated()
    {
        using var fixture = new Scratch();
        string folder = fixture.CreateSourceRecoveryFolder();
        for (int i = 0; i < 7; i++)
            File.WriteAllText(Path.Combine(folder,
                Guid.NewGuid().ToString("N") + ".json.tmp"), "SYNTHETIC");

        var result = new OfficeRecoveryStore(fixture.RecoveryRoot)
            .InspectIncomplete(fixture.Source, maxEntries: 3);

        Assert.IsTrue(result.Truncated);
        Assert.IsTrue(result.Candidates.Count <= 3);
        Assert.AreEqual(7, Directory.GetFiles(folder).Length);
    }

    [TestMethod]
    public void DifferentDocumentsNeverShareTheSameRecoveryListing()
    {
        using var fixture = new Scratch();
        string folder = fixture.CreateSourceRecoveryFolder();
        File.WriteAllText(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json.tmp"), "FIRST");
        string second = Path.Combine(fixture.Root, "other.docx");
        File.WriteAllText(second, "SECOND");

        var store = new OfficeRecoveryStore(fixture.RecoveryRoot);
        Assert.AreEqual(1, store.InspectIncomplete(fixture.Source).Candidates.Count);
        Assert.AreEqual(0, store.InspectIncomplete(second).Candidates.Count);
        Assert.IsTrue(File.Exists(second));
    }

    [TestMethod]
    public void LinkedRecoveryRootIsRejectedBeforeEnumeration()
    {
        using var fixture = new Scratch();
        string folder = fixture.CreateSourceRecoveryFolder();
        string alias = Path.Combine(fixture.Root, "recovery-symlink");
        Directory.CreateSymbolicLink(alias, fixture.RecoveryRoot);
        File.WriteAllText(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json.tmp"), "STAGED");

        var store = new OfficeRecoveryStore(alias);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            store.InspectIncomplete(fixture.Source));
        Assert.AreEqual(1, Directory.GetFiles(folder).Length);
    }

    [TestMethod]
    public void DanglingSymlinkIsReportedAsUntrustedWithoutFollowingIt()
    {
        using var fixture = new Scratch();
        string folder = fixture.CreateSourceRecoveryFolder();
        string missing = Path.Combine(fixture.Root, "missing-target.docx");
        string link = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".part.docx");
        File.CreateSymbolicLink(link, missing);

        var result = new OfficeRecoveryStore(fixture.RecoveryRoot).InspectIncomplete(fixture.Source);
        var item = result.Candidates.Single();
        Assert.AreEqual(OfficeRecoveryIncompleteStatus.UntrustedLinkOrNonFile, item.Status);
        Assert.IsNull(item.LengthBytes);
        Assert.IsNull(item.LastWriteUtc);
        Assert.IsFalse(File.Exists(missing));
    }

    [TestMethod]
    public void CancelledInventoryFailsInsteadOfReportingEmpty()
    {
        using var fixture = new Scratch();
        string folder = fixture.CreateSourceRecoveryFolder();
        string pending = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json.tmp");
        File.WriteAllText(pending, "PENDING");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var store = new OfficeRecoveryStore(fixture.RecoveryRoot);
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            store.InspectIncomplete(fixture.Source, cancellationToken: cancelled.Token));
        Assert.AreEqual("PENDING", File.ReadAllText(pending));
    }

    [TestMethod]
    public void InvalidBudgetAndUnsupportedFileTypeFailBeforeReadingAnything()
    {
        using var fixture = new Scratch();
        var store = new OfficeRecoveryStore(fixture.RecoveryRoot);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            store.InspectIncomplete(fixture.Source, maxEntries: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            store.InspectIncomplete(fixture.Source, maxEntries: 1025));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            store.InspectIncomplete(Path.Combine(fixture.Root, "other.pdf")));
        Assert.IsFalse(Directory.Exists(fixture.RecoveryRoot));
    }

    [TestMethod]
    public void RenamedFinalCopyWithoutManifestIsListedOnlyAsUnverifiedCandidate()
    {
        using var fixture = new Scratch();
        string folder = fixture.CreateSourceRecoveryFolder();
        string name = Guid.NewGuid().ToString("N") + ".docx";
        string orphan = Path.Combine(folder, name);
        File.WriteAllText(orphan, "SYNTHETIC_NOT_RESTORABLE");

        var inventory = new OfficeRecoveryStore(fixture.RecoveryRoot)
            .InspectIncomplete(fixture.Source);
        var candidate = inventory.Candidates.Single();

        Assert.AreEqual(name, candidate.FileName);
        Assert.AreEqual(OfficeRecoveryIncompleteKind.FinalCopyWithoutObservedManifest,
            candidate.Kind);
        Assert.AreEqual(OfficeRecoveryIncompleteStatus.RegularUnverified, candidate.Status);
        Assert.IsFalse(inventory.Truncated);
        Assert.AreEqual("SYNTHETIC_NOT_RESTORABLE", File.ReadAllText(orphan));
    }

    [TestMethod]
    public void FinalCopyWithEvenMalformedManifestIsNotCalledOrphan()
    {
        using var fixture = new Scratch();
        string folder = fixture.CreateSourceRecoveryFolder();
        string id = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(folder, id + ".docx"), "COPY_UNVERIFIED");
        File.WriteAllText(Path.Combine(folder, id + ".json"), "INVALID_JSON");

        var inventory = new OfficeRecoveryStore(fixture.RecoveryRoot)
            .InspectIncomplete(fixture.Source);
        Assert.AreEqual(0, inventory.Candidates.Count);
        Assert.IsFalse(inventory.Truncated);
    }

    [TestMethod]
    public void TruncatedEnumerationMustNotClaimFinalCopyHasNoManifest()
    {
        using var fixture = new Scratch();
        string folder = fixture.CreateSourceRecoveryFolder();
        string id = Guid.NewGuid().ToString("N");
        string copy = Path.Combine(folder, id + ".docx");
        string manifest = Path.Combine(folder, id + ".json");
        File.WriteAllText(copy, "COPY");
        File.WriteAllText(manifest, "MANIFEST");

        // Order is deterministic; the real enumerator is not.
        var inventory = new OfficeRecoveryStore(fixture.RecoveryRoot)
            .InspectIncompleteWithEnumerator(fixture.Source, 1, CancellationToken.None,
                _ => new[] { copy, manifest });

        Assert.IsTrue(inventory.Truncated);
        Assert.IsFalse(inventory.Candidates.Any(c =>
            c.Kind == OfficeRecoveryIncompleteKind.FinalCopyWithoutObservedManifest));
        Assert.AreEqual("COPY", File.ReadAllText(copy));
        Assert.AreEqual("MANIFEST", File.ReadAllText(manifest));
    }

    [TestMethod]
    public void FinalCopyForOtherFormatOrNonGuidNameIsNeverClassified()
    {
        using var fixture = new Scratch();
        string folder = fixture.CreateSourceRecoveryFolder();
        File.WriteAllText(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".xlsx"),
            "OTHER");
        File.WriteAllText(Path.Combine(folder, "not-a-guid.docx"), "FOREIGN");

        var inventory = new OfficeRecoveryStore(fixture.RecoveryRoot)
            .InspectIncomplete(fixture.Source);
        Assert.AreEqual(0, inventory.Candidates.Count);
        Assert.IsFalse(inventory.Truncated);
    }

    private sealed class Scratch : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "getdoc-gd31j27ao-readonly-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "synthetic.docx");
        public string RecoveryRoot => Path.Combine(Root, "recovery");

        public Scratch()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Source, "SYNTHETIC-NOT-A-DOCUMENT");
        }

        public string CreateSourceRecoveryFolder()
        {
            string source = Path.GetFullPath(Source);
            string normalized = OperatingSystem.IsWindows() ? source.ToUpperInvariant() : source;
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
            string folder = Path.Combine(RecoveryRoot, key);
            Directory.CreateDirectory(folder);
            return folder;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
