using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27bp: authored but NOT executed native no-follow tests.
/// All filesystem objects are synthetic; no real documents or release proof.
/// </summary>
[TestClass]
public sealed class RecoveryNativeLockNameProbeTests
{
    [TestMethod]
    public void RelativeLockPathAndClosedHandleAreRejected()
    {
        using var scratch = new Scratch();
        using var handle = File.OpenHandle(scratch.LockPath, FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryNativeLockNameProbe.VerifyCurrentName(
                handle, "relative-file.lock"));
        handle.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            RecoveryNativeLockNameProbe.VerifyCurrentName(
                handle, scratch.LockPath));
    }

    [TestMethod]
    public void CurrentLockNameMatchesPinnedHandleWhenNotReplaced()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        using var scratch = new Scratch();
        using var handle = File.OpenHandle(scratch.LockPath, FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var identity = RecoveryNativeLockNameProbe.VerifyCurrentName(
            handle, scratch.LockPath);
        Assert.IsTrue(identity.IsComplete);
        Assert.AreEqual(identity,
            RecoveryNativeHandleIdProbe.ReadFromExistingHandle(handle));
        Assert.AreEqual("LOCK_IDENTITY_SYNTHETIC", File.ReadAllText(scratch.LockPath));
    }

    [TestMethod]
    public void RenamedCurrentLockNameCannotImpersonateHeldHandle()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        using var scratch = new Scratch();
        using var handle = File.OpenHandle(scratch.LockPath, FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        string moved = Path.Combine(scratch.Root, "original-retained.lock");
        File.Move(scratch.LockPath, moved);
        File.WriteAllText(scratch.LockPath, "NEW_FOREIGN_LOCK");

        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeLockNameProbe.VerifyCurrentName(
                handle, scratch.LockPath));
        Assert.AreEqual("LOCK_IDENTITY_SYNTHETIC", File.ReadAllText(moved));
        Assert.AreEqual("NEW_FOREIGN_LOCK", File.ReadAllText(scratch.LockPath));
    }

    [TestMethod]
    public void SymlinkedNameIsRejectedWithoutReadingOrModifyingTarget()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        using var scratch = new Scratch();
        string protectedPath = Path.Combine(scratch.Root, "foreign.bin");
        File.WriteAllText(protectedPath, "KEEP_PROTECTED");
        using var handle = File.OpenHandle(scratch.LockPath, FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        string retained = Path.Combine(scratch.Root, "retained.lock");
        File.Move(scratch.LockPath, retained);
        try { File.CreateSymbolicLink(scratch.LockPath, protectedPath); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                   or PlatformNotSupportedException) { return; }

        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeLockNameProbe.VerifyCurrentName(
                handle, scratch.LockPath));
        Assert.AreEqual("KEEP_PROTECTED", File.ReadAllText(protectedPath));
        Assert.AreEqual("LOCK_IDENTITY_SYNTHETIC", File.ReadAllText(retained));
    }

    [TestMethod]
    public void MissingNameCannotBeRecreatedByMetadataProbe()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        using var scratch = new Scratch();
        using var handle = File.OpenHandle(scratch.LockPath, FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        string retained = Path.Combine(scratch.Root, "retained.lock");
        File.Move(scratch.LockPath, retained);
        Assert.ThrowsExactly<IOException>(() =>
            RecoveryNativeLockNameProbe.VerifyCurrentName(
                handle, scratch.LockPath));
        Assert.IsFalse(File.Exists(scratch.LockPath));
        Assert.AreEqual("LOCK_IDENTITY_SYNTHETIC", File.ReadAllText(retained));
    }

    private sealed class Scratch : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(),
            "getdoc-gd31j27bp-name-" + Guid.NewGuid().ToString("N"));
        internal string LockPath => Path.Combine(Root, "capture.lock");
        internal Scratch()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(LockPath, "LOCK_IDENTITY_SYNTHETIC");
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
