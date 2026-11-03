using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27bk: deterministic synthetic path/metadata tests only.
/// They do not simulate a real kill, confirm deletion safety or enable a reaper.
/// </summary>
[TestClass]
public sealed class RecoveryDetachedWorkspaceInspectorTests
{
    [TestMethod]
    public void MissingRootReturnsEmptyWithoutCreatingAnyDirectory()
    {
        using var scratch = new Scratch();
        string absent = Path.Combine(scratch.Root, "does-not-exist");
        var inventory = RecoveryDetachedWorkspaceInspector.Inspect(absent);

        Assert.AreEqual(0, inventory.Candidates.Count);
        Assert.IsFalse(inventory.Truncated);
        Assert.IsFalse(Directory.Exists(absent));
    }

    [TestMethod]
    public void OnlyRecognizedPrivateNamesAreListedWithoutOpeningContents()
    {
        using var scratch = new Scratch();
        string publisher = scratch.Directory("getdoc-recovery-private-abcd1234.abc");
        string consumer = scratch.Directory("getdoc-consumer-owned-A1b2C3d4.abc");
        string unknown = scratch.Directory("getdoc-other-private-abcd1234");
        string userFile = Path.Combine(publisher, "keep-user-bytes.docx");
        File.WriteAllText(userFile, "NEVER_READ_OR_REMOVE");

        var inventory = RecoveryDetachedWorkspaceInspector.Inspect(scratch.Root);
        Assert.AreEqual(2, inventory.Candidates.Count);
        Assert.IsFalse(inventory.Truncated);
        Assert.IsTrue(inventory.Candidates.Any(c =>
            c.DirectoryName == Path.GetFileName(publisher) &&
            c.Kind == RecoveryDetachedWorkspaceKind.PrivatePublication &&
            c.Status == RecoveryDetachedWorkspaceStatus.DirectoryUnverified));
        Assert.IsTrue(inventory.Candidates.Any(c =>
            c.DirectoryName == Path.GetFileName(consumer) &&
            c.Kind == RecoveryDetachedWorkspaceKind.OwnedConsumer &&
            c.Status == RecoveryDetachedWorkspaceStatus.DirectoryUnverified));
        Assert.IsFalse(inventory.Candidates.Any(c => c.DirectoryName == Path.GetFileName(unknown)));
        Assert.AreEqual("NEVER_READ_OR_REMOVE", File.ReadAllText(userFile));
    }

    [TestMethod]
    public void PrefixedRegularFileIsNeverMistakenForPrivateWorkspace()
    {
        using var scratch = new Scratch();
        string file = Path.Combine(scratch.Root, "getdoc-consumer-owned-abc12345");
        File.WriteAllText(file, "KEEP");

        var inventory = RecoveryDetachedWorkspaceInspector.Inspect(scratch.Root);
        Assert.AreEqual(1, inventory.Candidates.Count);
        Assert.AreEqual(RecoveryDetachedWorkspaceStatus.UntrustedLinkOrNonDirectory,
            inventory.Candidates[0].Status);
        Assert.AreEqual("KEEP", File.ReadAllText(file));
    }

    [TestMethod]
    public void EnumerationBudgetIncludesUnrelatedEntriesAndReportsTruncation()
    {
        using var scratch = new Scratch();
        string unrelated1 = scratch.Directory("foreign-one");
        string unrelated2 = scratch.Directory("foreign-two");
        string target = scratch.Directory("getdoc-recovery-private-abcdef123");
        var ordered = new[] { unrelated1, unrelated2, target };

        var inventory = RecoveryDetachedWorkspaceInspector.InspectWithEnumerator(
            scratch.Root, maxEntries: 2, CancellationToken.None, _ => ordered);

        Assert.IsTrue(inventory.Truncated);
        Assert.AreEqual(0, inventory.Candidates.Count,
            "A partial scan must never claim all private directories were examined.");
        Assert.IsTrue(Directory.Exists(target));
    }

    [TestMethod]
    public void ForeignOrNestedEnumeratorPathIsRejectedBeforeAnyMetadataRead()
    {
        using var scratch = new Scratch();
        string outside = scratch.Directory("getdoc-recovery-private-abc1234");
        string nested = Path.Combine(outside, "getdoc-consumer-owned-abc1234");
        Directory.CreateDirectory(nested);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryDetachedWorkspaceInspector.InspectWithEnumerator(
                scratch.Root, 10, CancellationToken.None, _ => new[] { nested }));
    }

    [TestMethod]
    public void LazyEnumerationIOExceptionPropagatesInsteadOfReportingEmptyScan()
    {
        using var scratch = new Scratch();
        static IEnumerable<string> ThrowingEnumerator(string root)
        {
            yield return Path.Combine(root, "unrelated");
            throw new IOException("synthetic MoveNext failure");
        }

        Assert.ThrowsExactly<IOException>(() =>
            RecoveryDetachedWorkspaceInspector.InspectWithEnumerator(
                scratch.Root, 10, CancellationToken.None, ThrowingEnumerator));
    }

    [TestMethod]
    public void CancelledScanDoesNotEnumerateOrMutate()
    {
        using var scratch = new Scratch();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        bool entered = false;

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            RecoveryDetachedWorkspaceInspector.InspectWithEnumerator(
                scratch.Root, 10, cancel.Token, _ =>
                {
                    entered = true;
                    return Array.Empty<string>();
                }));
        Assert.IsFalse(entered);
    }

    [TestMethod]
    public void LinkedWorkspaceIsMarkedUntrustedIfSymlinksAreAvailable()
    {
        using var scratch = new Scratch();
        string real = scratch.Directory("real-empty");
        string link = Path.Combine(scratch.Root, "getdoc-recovery-private-abcdef12");
        try
        {
            Directory.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or PlatformNotSupportedException)
        {
            return; // Physical symlink tests are a separate not-run platform gate.
        }

        var inventory = RecoveryDetachedWorkspaceInspector.Inspect(scratch.Root);
        Assert.AreEqual(1, inventory.Candidates.Count);
        Assert.AreEqual(RecoveryDetachedWorkspaceStatus.UntrustedLinkOrNonDirectory,
            inventory.Candidates[0].Status);
        Assert.IsTrue(Directory.Exists(real));
    }

    [TestMethod]
    public void LinkedTemporaryRootIsRejectedIfSymlinksAreAvailable()
    {
        using var scratch = new Scratch();
        string real = scratch.Directory("real-root");
        string linkedRoot = Path.Combine(scratch.Root, "linked-root");
        try
        {
            Directory.CreateSymbolicLink(linkedRoot, real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or PlatformNotSupportedException)
        {
            return; // Covered by manual real-platform gate when unsupported.
        }

        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryDetachedWorkspaceInspector.Inspect(linkedRoot));
        Assert.IsTrue(Directory.Exists(real));
    }

    private sealed class Scratch : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(),
            "getdoc-bk-inventory-" + Guid.NewGuid().ToString("N"));

        internal Scratch() => System.IO.Directory.CreateDirectory(Root);

        internal string Directory(string name)
        {
            string path = Path.Combine(Root, name);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Root))
                System.IO.Directory.Delete(Root, recursive: true);
        }
    }
}
