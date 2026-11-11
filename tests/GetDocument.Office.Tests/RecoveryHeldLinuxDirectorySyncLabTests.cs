using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27ca block 3: native directory fsync is diagnostic and never
/// evidence of full journal durability or rename-safe ownership.
/// </summary>
[TestClass]
public sealed class RecoveryHeldLinuxDirectorySyncLabTests
{
    private static RecoveryLinuxParentEvidence Private(
        ulong id = 1, uint uid = 1000, ushort mode = 0x41C0, ulong mount = 8) =>
        new(new RecoveryNativeFileId(RecoveryNativePlatform.Linux, 77, id, 0),
            uid, mode, mount);

    [TestMethod]
    public void MatchingPrivateParentEvidenceIsOnlyDiagnostic()
    {
        var observation = RecoveryHeldLinuxDirectorySyncLab.Evaluate(
            Private(), Private(), 1000);
        Assert.AreEqual(RecoveryHeldDirectorySyncFinding.MatchedButUntrusted,
            observation);
        Assert.IsFalse(RecoveryHeldLinuxDirectorySyncLab.ProductionDirectorySyncEnabled);
        Assert.IsFalse(RecoveryHeldLinuxDirectorySyncLab.CanAuthorizeJournalCommit);
    }

    [TestMethod]
    public void ChangedFileIdFailsClosed()
    {
        Assert.AreEqual(RecoveryHeldDirectorySyncFinding.ChangedDirectory,
            RecoveryHeldLinuxDirectorySyncLab.Evaluate(Private(), Private(id: 2), 1000));
    }

    [TestMethod]
    public void DifferentMountFailsClosed()
    {
        Assert.AreEqual(RecoveryHeldDirectorySyncFinding.ChangedDirectory,
            RecoveryHeldLinuxDirectorySyncLab.Evaluate(Private(), Private(mount: 9), 1000));
    }

    [TestMethod]
    public void ForeignOwnerIsRejected()
    {
        Assert.AreEqual(RecoveryHeldDirectorySyncFinding.IneligibleDirectory,
            RecoveryHeldLinuxDirectorySyncLab.Evaluate(Private(uid: 42), Private(uid: 42), 1000));
    }

    [TestMethod]
    public void GroupWritableDirectoryIsRejected()
    {
        Assert.AreEqual(RecoveryHeldDirectorySyncFinding.IneligibleDirectory,
            RecoveryHeldLinuxDirectorySyncLab.Evaluate(
                Private(mode: 0x41F0), Private(mode: 0x41F0), 1000));
    }

    [TestMethod]
    public void RegularFileCannotPretendToBeParent()
    {
        Assert.AreEqual(RecoveryHeldDirectorySyncFinding.IneligibleDirectory,
            RecoveryHeldLinuxDirectorySyncLab.Evaluate(
                Private(mode: 0x81C0), Private(mode: 0x81C0), 1000));
    }

    [TestMethod]
    public void MissingMountOrNativeIdFailsClosed()
    {
        Assert.AreEqual(RecoveryHeldDirectorySyncFinding.IneligibleDirectory,
            RecoveryHeldLinuxDirectorySyncLab.Evaluate(
                Private(mount: 0), Private(mount: 0), 1000));
        Assert.AreEqual(RecoveryHeldDirectorySyncFinding.IneligibleDirectory,
            RecoveryHeldLinuxDirectorySyncLab.Evaluate(default, default, 1000));
    }

    [TestMethod]
    public void RejectsWindowsIdentityEvenWithSimulatedOwner()
    {
        var windows = Private() with
        {
            FileId = new RecoveryNativeFileId(RecoveryNativePlatform.Windows, 77, 1, 23)
        };
        Assert.AreEqual(RecoveryHeldDirectorySyncFinding.IneligibleDirectory,
            RecoveryHeldLinuxDirectorySyncLab.Evaluate(windows, windows, 1000));
    }

    [TestMethod]
    public void InvalidSafeHandleIsRefusedBeforeLinuxSyscalls()
    {
        using var invalid = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            RecoveryHeldLinuxDirectorySyncLab.SyncExistingDirectoryForLab(invalid));
    }

    [TestMethod]
    public void CancelledOperationNeverApprovesSync()
    {
        using var invalid = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            RecoveryHeldLinuxDirectorySyncLab.SyncExistingDirectoryForLab(
                invalid, cancel.Token));
        Assert.IsFalse(RecoveryHeldLinuxDirectorySyncLab.CanAuthorizeJournalCommit);
    }

    [TestMethod]
    public void WindowsAndUnknownPlatformAreNeverClaimedSupported()
    {
        Assert.IsFalse(RecoveryHeldLinuxDirectorySyncLab.WindowsDirectorySyncImplemented);
        Assert.IsFalse(RecoveryHeldLinuxDirectorySyncLab.SmbDurabilityVerified);
    }
}
