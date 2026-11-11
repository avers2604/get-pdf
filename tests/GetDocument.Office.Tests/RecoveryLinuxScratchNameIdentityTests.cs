using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.Tests;

/// <summary>GD-31J.27cd: Linux scratch basename to held inode, read-only.</summary>
[TestClass]
public sealed class RecoveryLinuxScratchNameIdentityTests
{
    private static RecoveryNativeFileId Id(ulong inode, ulong device = 11) =>
        new(RecoveryNativePlatform.Linux, device, inode, 0);
    private static RecoveryLinuxParentEvidence Parent(ulong inode = 1, ulong mount = 19) =>
        new(Id(inode), 1000, 0x41C0, mount);
    private static RecoveryLinuxRegularEntryObservation File(ulong inode = 2, ulong mount = 19,
        ulong device = 11, uint links = 1) =>
        new(Id(inode, device), mount, 1000, links);

    private static RecoveryLinuxScratchNameEvidence Stable() =>
        new(Parent(), Parent(), File(), File(), File(), File());

    [TestMethod]
    public void MatchingEvidenceIsOnlyAnObservationNotAuthority()
    {
        var finding = RecoveryLinuxScratchNameIdentityProbe.Evaluate(Stable());
        Assert.AreEqual(RecoveryScratchNameFinding.MatchedAtObservation, finding);
        Assert.IsFalse(RecoveryLinuxScratchNameIdentityProbe.ProductionNameAuthorityEnabled);
        Assert.IsFalse(RecoveryLinuxScratchNameIdentityProbe.CanAuthorizeCleanup);
    }

    [TestMethod]
    public void ParentSubstitutionIsDetected()
    {
        Assert.AreEqual(RecoveryScratchNameFinding.ParentChanged,
            RecoveryLinuxScratchNameIdentityProbe.Evaluate(Stable() with {
                ParentAfter = Parent(inode: 8)
            }));
    }

    [TestMethod]
    public void NameReplacedDuringFirstLookupIsDetected()
    {
        Assert.AreEqual(RecoveryScratchNameFinding.NameDoesNotMatchHeld,
            RecoveryLinuxScratchNameIdentityProbe.Evaluate(Stable() with {
                NamedFirst = File(99)
            }));
    }

    [TestMethod]
    public void NameReplacedBetweenTwoOpenat2CallsIsDetected()
    {
        Assert.AreEqual(RecoveryScratchNameFinding.NameDoesNotMatchHeld,
            RecoveryLinuxScratchNameIdentityProbe.Evaluate(Stable() with {
                NamedSecond = File(100)
            }));
    }

    [TestMethod]
    public void HeldFileChangedAfterNameLookupIsDetected()
    {
        Assert.AreEqual(RecoveryScratchNameFinding.NameDoesNotMatchHeld,
            RecoveryLinuxScratchNameIdentityProbe.Evaluate(Stable() with {
                HeldAfter = File(101)
            }));
    }

    [TestMethod]
    public void SymlinkOrInvalidStatxEvidenceCannotPass()
    {
        Assert.AreEqual(RecoveryScratchNameFinding.IncompleteEvidence,
            RecoveryLinuxScratchNameIdentityProbe.Evaluate(Stable() with {
                NamedFirst = default
            }));
        Assert.AreEqual(RecoveryScratchNameFinding.IncompleteEvidence,
            RecoveryLinuxScratchNameIdentityProbe.Evaluate(Stable() with {
                NamedSecond = File(2, links: 2)
            }));
    }

    [TestMethod]
    public void MountOrDeviceChangeIsDetected()
    {
        Assert.AreEqual(RecoveryScratchNameFinding.CrossVolumeOrMount,
            RecoveryLinuxScratchNameIdentityProbe.Evaluate(Stable() with {
                NamedSecond = File(2, mount: 20)
            }));
        Assert.AreEqual(RecoveryScratchNameFinding.CrossVolumeOrMount,
            RecoveryLinuxScratchNameIdentityProbe.Evaluate(Stable() with {
                NamedFirst = File(2, device: 33)
            }));
    }

    [TestMethod]
    public void AliasOfParentIsRejected()
    {
        Assert.AreEqual(RecoveryScratchNameFinding.NameDoesNotMatchHeld,
            RecoveryLinuxScratchNameIdentityProbe.Evaluate(Stable() with {
                HeldBefore = File(1), HeldAfter = File(1),
                NamedFirst = File(1), NamedSecond = File(1)
            }));
    }

    [TestMethod]
    public void InvalidBasenameIsRefusedBeforeNativeLookup()
    {
        using var invalid = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryLinuxScratchNameIdentityProbe.ObserveExisting(
                invalid, invalid, "../not-scratch"));
    }

    [TestMethod]
    public void InvalidHandlesCannotTriggerNativeLookup()
    {
        using var invalid = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            RecoveryLinuxScratchNameIdentityProbe.ObserveExisting(
                invalid, invalid, Guid.NewGuid().ToString("N") + ".gdjournal.tmp"));
    }

    [TestMethod]
    public void ObservationCannotAuthorizeAnyMutation()
    {
        var record = Stable();
        Assert.IsFalse(record.CanRestore);
        Assert.IsFalse(record.CanDelete);
        Assert.IsFalse(record.CanPublish);
        Assert.IsFalse(record.HasTrustedAncestry);
    }

    [TestMethod]
    public void ExplicitOptInLinuxScratchCanBeObservedByNameWithoutDeletion()
    {
        if (!OperatingSystem.IsLinux() ||
            Environment.GetEnvironmentVariable("GD31J27CD_NATIVE_LAB") != "1" ||
            Environment.GetEnvironmentVariable("GD31J27CB_NATIVE_LAB") != "1")
            return;
        // No generic /tmp or pathname fallback: operator must provide an
        // already-open private 0700 parent fd in a separate platform harness.
        Assert.IsFalse(RecoveryLinuxScratchNameIdentityProbe.CanAuthorizeCleanup);
    }
}
