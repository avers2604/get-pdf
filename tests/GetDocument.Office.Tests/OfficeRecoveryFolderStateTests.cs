using System.Security;
using System.Security.Cryptography;
using System.Text;
using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27ay: synthetic directory-state and lazy-enumeration fault tests.
/// All paths are disposable test locations; no real GET documents are used.
/// </summary>
[TestClass]
public sealed class OfficeRecoveryFolderStateTests
{
    [TestMethod]
    public void MissingRecoveryFolderMeansEmptyOnlyWhenActuallyAbsent()
    {
        using var scratch = new Scratch();
        var store = new OfficeRecoveryStore(scratch.RecoveryRoot);
        Assert.IsNull(store.FindLatest(scratch.Source));
        var inventory = store.InspectIncomplete(scratch.Source);
        Assert.AreEqual(0, inventory.Candidates.Count);
        Assert.IsFalse(inventory.Truncated);
        Assert.IsFalse(Directory.Exists(scratch.RecoveryRoot));
    }

    [TestMethod]
    public void VerifiedEmptyFolderMeansNoSnapshotButCreatesNothing()
    {
        using var scratch = new Scratch();
        scratch.CreateRecoveryFolder();
        var store = new OfficeRecoveryStore(scratch.RecoveryRoot);
        Assert.IsNull(store.FindLatest(scratch.Source));
        var inventory = store.InspectIncomplete(scratch.Source);
        Assert.AreEqual(0, inventory.Candidates.Count);
        Assert.IsFalse(inventory.Truncated);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(scratch.RecoveryFolder).Length);
    }

    [TestMethod]
    public void OnlyExplicitMissingErrorsAreAllowedToMeanNoFolder()
    {
        Assert.IsFalse(OfficeRecoveryStore.RecoveryFolderExists(
            "synthetic", _ => throw new FileNotFoundException("absent")));
        Assert.IsFalse(OfficeRecoveryStore.RecoveryFolderExists(
            "synthetic", _ => throw new DirectoryNotFoundException("absent parent")));
        Assert.IsTrue(OfficeRecoveryStore.RecoveryFolderExists(
            "synthetic", _ => FileAttributes.Directory));
    }

    [TestMethod]
    public void AccessDeniedAndSecurityErrorCannotMasqueradeAsEmpty()
    {
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            OfficeRecoveryStore.RecoveryFolderExists(
                "synthetic", _ => throw new UnauthorizedAccessException("denied")));
        Assert.ThrowsExactly<SecurityException>(() =>
            OfficeRecoveryStore.RecoveryFolderExists(
                "synthetic", _ => throw new SecurityException("policy")));
        Assert.ThrowsExactly<IOException>(() =>
            OfficeRecoveryStore.RecoveryFolderExists(
                "synthetic", _ => throw new IOException("SMB unavailable")));
    }

    [TestMethod]
    public void NonDirectoryAndReparseEntriesCannotMasqueradeAsEmpty()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            OfficeRecoveryStore.RecoveryFolderExists(
                "synthetic", _ => FileAttributes.Normal));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            OfficeRecoveryStore.RecoveryFolderExists(
                "synthetic", _ => FileAttributes.Directory | FileAttributes.ReparsePoint));
    }

    [TestMethod]
    public void FindLatestFailsClosedWhenFolderDisappearsDuringLazyEnumeration()
    {
        using var scratch = new Scratch();
        scratch.CreateRecoveryFolder();
        var store = new OfficeRecoveryStore(scratch.RecoveryRoot);
        Assert.ThrowsExactly<DirectoryNotFoundException>(() =>
            store.FindLatestWithEnumerator(scratch.Source, folder => VanishingEntries(folder)));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(scratch.RecoveryFolder).Length);
    }

    [TestMethod]
    public void InspectFailsClosedWhenFolderDisappearsDuringLazyEnumeration()
    {
        using var scratch = new Scratch();
        scratch.CreateRecoveryFolder();
        var store = new OfficeRecoveryStore(scratch.RecoveryRoot);
        Assert.ThrowsExactly<DirectoryNotFoundException>(() =>
            store.InspectIncompleteWithEnumerator(scratch.Source, 256,
                CancellationToken.None, folder => VanishingEntries(folder)));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(scratch.RecoveryFolder).Length);
    }

    [TestMethod]
    public void DirectoryEnumerationAccessDenialNeverBecomesCleanInventory()
    {
        using var scratch = new Scratch();
        scratch.CreateRecoveryFolder();
        var store = new OfficeRecoveryStore(scratch.RecoveryRoot);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            store.FindLatestWithEnumerator(scratch.Source,
                _ => throw new UnauthorizedAccessException("Synthetic ACL")));
        Assert.ThrowsExactly<SecurityException>(() =>
            store.InspectIncompleteWithEnumerator(scratch.Source, 256,
                CancellationToken.None,
                _ => throw new SecurityException("Synthetic policy")));
    }

    private static IEnumerable<string> VanishingEntries(string folder)
    {
        // The iterator is deliberately lazy. The folder is present at the
        // initial GetAttributes probe but disappears from the logical scan.
        yield return Path.Combine(folder, "not-a-guid.json");
        throw new DirectoryNotFoundException("Synthetic directory loss after enumerator began");
    }

    private sealed class Scratch : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "getdoc-gd31j27ay-" + Guid.NewGuid().ToString("N"));

        public string Source => Path.Combine(_root, "synthetic.docx");
        public string RecoveryRoot => Path.Combine(_root, "recovery");
        public string RecoveryFolder
        {
            get
            {
                string canonical = Path.GetFullPath(Source);
                string normalized = OperatingSystem.IsWindows()
                    ? canonical.ToUpperInvariant() : canonical;
                string hash = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
                return Path.Combine(RecoveryRoot, hash);
            }
        }

        public Scratch()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Source, "SYNTHETIC PATH ONLY");
        }

        public void CreateRecoveryFolder() => Directory.CreateDirectory(RecoveryFolder);

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
