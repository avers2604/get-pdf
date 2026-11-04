using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27bn: source-isolated fake OS handle evidence. Does not exercise
/// Windows handles, Linux openat2/fstat or actual root/lock-file unlink races.
/// </summary>
[TestClass]
public sealed class RecoveryCaptureIdentityAttestationTests
{
    [TestMethod]
    public void RealAdaptersRefuseToClaimNativeRootAndLockIdentity()
    {
        var root = RecoveryCaptureIdentityRequest.Create(
            Path.Combine(Path.GetTempPath(), "synthetic-recovery"));
        Assert.ThrowsExactly<PlatformNotSupportedException>(() =>
            RecoveryCaptureIdentityGate.InspectForPlanning(
                new WindowsRecoveryCaptureIdentityAdapter(), root));
        Assert.ThrowsExactly<PlatformNotSupportedException>(() =>
            RecoveryCaptureIdentityGate.InspectForPlanning(
                new LinuxRecoveryCaptureIdentityAdapter(), root));
    }

    [TestMethod]
    public void InvalidOrRelativeRootIsRejectedBeforeCallingAdapter()
    {
        var adapter = new FakeAdapter(GoodEvidence());
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryCaptureIdentityRequest.Create("relative-root"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryCaptureIdentityRequest.Create(""));
        Assert.IsFalse(adapter.Called);
    }

    [TestMethod]
    public void ParentReplacedAfterOpenFailsEvenIfFileHandleStayedStable()
    {
        var evidence = GoodEvidence() with
        {
            RootAtRecheck = new RecoveryNativeFileId(
                RecoveryNativePlatform.Windows, 41, 400, 9)
        };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryCaptureIdentityGate.InspectForPlanning(
                new FakeAdapter(evidence), NewRoot()));
    }

    [TestMethod]
    public void LockPathReplacedWhileOriginalHandleRemainsOpenFails()
    {
        var evidence = GoodEvidence() with
        {
            LockNameAtRecheck = new RecoveryNativeFileId(
                RecoveryNativePlatform.Windows, 41, 700, 8)
        };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryCaptureIdentityGate.InspectForPlanning(
                new FakeAdapter(evidence), NewRoot()));
    }

    [TestMethod]
    public void MissingNoFollowExclusiveParentOrHeldLockFailsClosed()
    {
        foreach (var invalid in new[]
        {
            GoodEvidence() with { NoFollowEnforced = false },
            GoodEvidence() with { ParentWriterIsolationConfirmed = false },
            GoodEvidence() with { RootOpenedRelativeToTrustedAnchor = false },
            GoodEvidence() with { LockRangeHeld = false },
            GoodEvidence() with { LockHandlePinned = false },
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                RecoveryCaptureIdentityGate.InspectForPlanning(
                    new FakeAdapter(invalid), NewRoot()));
    }

    [TestMethod]
    public void IdentityPlatformVolumeMismatchAndIncompleteFileIdFail()
    {
        var mismatch = GoodEvidence() with { Platform = RecoveryNativePlatform.Linux };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryCaptureIdentityGate.InspectForPlanning(
                new FakeAdapter(mismatch), NewRoot()));
        var wrongVolume = GoodEvidence() with
        {
            LockAtOpen = new RecoveryNativeFileId(
                RecoveryNativePlatform.Windows, 99, 500, 8),
            LockNameAtRecheck = new RecoveryNativeFileId(
                RecoveryNativePlatform.Windows, 99, 500, 8),
        };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryCaptureIdentityGate.InspectForPlanning(
                new FakeAdapter(wrongVolume), NewRoot()));
        var zero = GoodEvidence() with
        {
            LockAtOpen = new RecoveryNativeFileId(
                RecoveryNativePlatform.Windows, 0, 0, 0)
        };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryCaptureIdentityGate.InspectForPlanning(
                new FakeAdapter(zero), NewRoot()));
    }

    [TestMethod]
    public void ConsistentFakeIdentityIsOnlyAnAuditObservationNotDeleteAuthority()
    {
        var adapter = new FakeAdapter(GoodEvidence());
        var evidence = RecoveryCaptureIdentityGate.InspectForPlanning(
            adapter, NewRoot());
        Assert.IsTrue(adapter.Called);
        Assert.AreEqual(GoodEvidence(), evidence);
        Assert.IsTrue(evidence.ParentWriterIsolationConfirmed);
    }

    private static RecoveryCaptureIdentityRequest NewRoot() =>
        RecoveryCaptureIdentityRequest.Create(
            Path.Combine(Path.GetTempPath(), "synthetic-recovery"));

    private static RecoveryCaptureIdentityEvidence GoodEvidence()
    {
        var root = new RecoveryNativeFileId(
            RecoveryNativePlatform.Windows, 41, 200, 8);
        var file = new RecoveryNativeFileId(
            RecoveryNativePlatform.Windows, 41, 500, 8);
        return new RecoveryCaptureIdentityEvidence(
            RecoveryNativePlatform.Windows,
            root, root,
            file, file, file,
            RootOpenedRelativeToTrustedAnchor: true,
            NoFollowEnforced: true,
            ParentWriterIsolationConfirmed: true,
            LockHandlePinned: true,
            LockRangeHeld: true);
    }

    private sealed class FakeAdapter : IRecoveryCaptureIdentityAdapter
    {
        private readonly RecoveryCaptureIdentityEvidence _evidence;
        internal bool Called { get; private set; }
        internal FakeAdapter(RecoveryCaptureIdentityEvidence evidence) =>
            _evidence = evidence;
        public RecoveryNativePlatform Platform => RecoveryNativePlatform.Windows;
        public bool SupportsVerifiedRootAndLockIdentity => true;
        public RecoveryCaptureIdentityEvidence ObserveHeldIdentity(
            RecoveryCaptureIdentityRequest root)
        {
            Called = true;
            return _evidence;
        }
    }
}
