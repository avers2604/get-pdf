using System.Buffers.Binary;
using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27bq authored tests, NOT executed. OS-gated cases are not an
/// offline Linux/Astra qualification or proof of a trusted filesystem root.
/// </summary>
[TestClass]
public sealed class RecoveryPinnedLinuxParentTests
{
    [TestMethod]
    public void PrivateDirectoryStatxParserAcceptsComplete0700OwnedEvidence()
    {
        RecoveryLinuxParentEvidence evidence =
            RecoveryPinnedLinuxParentProbe.ParseDirectoryStatx(SyntheticDirectory());
        Assert.AreEqual(1234u, evidence.OwnerUid);
        Assert.AreEqual(42UL, evidence.MountId);
        Assert.AreEqual(0x1C0, evidence.Mode & 0x0FFF);
        Assert.IsTrue(evidence.FileId.IsComplete);
        RecoveryPinnedLinuxParentProbe.RequirePrivateOwner(evidence, 1234);
    }

    [TestMethod]
    public void DirectoryParserRejectsIncompleteStatxAndSymlink()
    {
        byte[] item = SyntheticDirectory();
        BinaryPrimitives.WriteUInt32LittleEndian(item.AsSpan(0, 4), 0x101);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryPinnedLinuxParentProbe.ParseDirectoryStatx(item));
        item = SyntheticDirectory();
        BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(28, 2), 0xA1C0);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryPinnedLinuxParentProbe.ParseDirectoryStatx(item));
    }

    [TestMethod]
    public void PrivateOwnerGuardRejectsWrongUidOrBroaderPermissions()
    {
        RecoveryLinuxParentEvidence good =
            RecoveryPinnedLinuxParentProbe.ParseDirectoryStatx(SyntheticDirectory());
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryPinnedLinuxParentProbe.RequirePrivateOwner(good, 999));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryPinnedLinuxParentProbe.RequirePrivateOwner(
                good with { Mode = 0x41ED }, 1234));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryPinnedLinuxParentProbe.RequirePrivateOwner(
                good with { Mode = 0x41C8 }, 1234));
    }

    [TestMethod]
    public void ChildNameGrammarRejectsPathsAndUnknownNames()
    {
        foreach (string name in new[]
        {
            "", ".", "..", "../coordinator", "/tmp/coordinator",
            "recovery-volume-coordinator/other", "recovery-volume-coordinator\\other",
            "foreign-app", "C:lock"
        })
            Assert.ThrowsExactly<ArgumentException>(() =>
                RecoveryPinnedLinuxParentProbe.ValidateChildName(name));

        RecoveryPinnedLinuxParentProbe.ValidateChildName("recovery-volume-coordinator");
        RecoveryPinnedLinuxParentProbe.ValidateChildName(new string('a', 64));
    }

    [TestMethod]
    public void LinuxOnlyRelativeOpenRejectsAlreadyClosedParent()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scratch = new Scratch();
        using var parent = File.OpenHandle(scratch.Anchor, FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        parent.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            RecoveryPinnedLinuxParentProbe.OpenChildRelative(
                parent, "recovery-volume-coordinator"));
    }

    [TestMethod]
    public void LinuxRelativeOpenPinsAnExistingPrivateChildNoPathReopen()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scratch = new Scratch();
        using var parent = File.OpenHandle(scratch.Anchor, FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var child = RecoveryPinnedLinuxParentProbe.OpenChildRelative(
            parent, "recovery-volume-coordinator");
        Assert.IsTrue(child.ParentAtOpen.FileId.IsComplete);
        Assert.AreEqual(child.ParentAtOpen, child.ParentAtRecheck);
        Assert.AreEqual(child.ParentAtOpen.MountId, child.Child.MountId);
        Assert.AreEqual(child.ParentAtOpen.OwnerUid, child.Child.OwnerUid);
        Assert.IsTrue(child.Child.FileId.IsComplete);
    }

    [TestMethod]
    public void LinuxRelativeOpenRefusesSymlinkChildAndPreservesTarget()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scratch = new Scratch();
        Directory.Delete(scratch.Child);
        string external = Path.Combine(scratch.Root, "other-directory");
        Directory.CreateDirectory(external, UnixFileMode.UserRead |
            UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateSymbolicLink(scratch.Child, external);
        using var parent = File.OpenHandle(scratch.Anchor, FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Assert.ThrowsExactly<IOException>(() =>
            RecoveryPinnedLinuxParentProbe.OpenChildRelative(
                parent, "recovery-volume-coordinator"));
        Assert.IsTrue(Directory.Exists(external));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(external).Length);
    }

    private static byte[] SyntheticDirectory()
    {
        byte[] bytes = new byte[256];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x111B);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), 1234);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28, 2), 0x41C0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32, 8), 123456);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(136, 4), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(140, 4), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(144, 8), 42);
        return bytes;
    }

    private sealed class Scratch : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(),
            "getdoc-bq-linux-" + Guid.NewGuid().ToString("N"));
        internal string Anchor => Path.Combine(Root, "trusted-anchor");
        internal string Child => Path.Combine(Anchor, "recovery-volume-coordinator");

        internal Scratch()
        {
            if (!OperatingSystem.IsLinux()) return;
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Anchor, UnixFileMode.UserRead |
                UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(Child, UnixFileMode.UserRead |
                UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
