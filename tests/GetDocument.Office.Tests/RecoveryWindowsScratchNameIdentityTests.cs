using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.Tests;

/// <summary>GD-31J.27cd: read-only Windows NT name reopen evidence.</summary>
[TestClass]
public sealed class RecoveryWindowsScratchNameIdentityTests
{
    private static RecoveryNativeFileId Id(ulong file, ulong volume = 55) =>
        new(RecoveryNativePlatform.Windows, volume, file, 77);

    private static RecoveryWindowsScratchNameEvidence Stable() =>
        new(Id(1), Id(1), Id(2), Id(2), Id(2), Id(2),
            FirstReparse: false, SecondReparse: false);

    [TestMethod]
    public void StableWindowsNameAndHandleRemainOnlyObserved()
    {
        Assert.AreEqual(RecoveryWindowsScratchNameFinding.MatchedAtObservation,
            RecoveryWindowsScratchNameIdentityProbe.Evaluate(Stable()));
        Assert.IsFalse(RecoveryWindowsScratchNameIdentityProbe.ProductionNameAuthorityEnabled);
        Assert.IsFalse(RecoveryWindowsScratchNameIdentityProbe.CanAuthorizeCleanup);
    }

    [TestMethod]
    public void ParentChangedRejectsNameBinding()
    {
        Assert.AreEqual(RecoveryWindowsScratchNameFinding.ParentChanged,
            RecoveryWindowsScratchNameIdentityProbe.Evaluate(Stable() with
            {
                ParentAfter = Id(9)
            }));
    }

    [TestMethod]
    public void NameSubstitutionAtFirstOpenRejects()
    {
        Assert.AreEqual(RecoveryWindowsScratchNameFinding.NameDoesNotMatchHeld,
            RecoveryWindowsScratchNameIdentityProbe.Evaluate(Stable() with
            {
                NamedFirst = Id(8)
            }));
    }

    [TestMethod]
    public void NameSubstitutionBetweenNtOpensRejects()
    {
        Assert.AreEqual(RecoveryWindowsScratchNameFinding.NameDoesNotMatchHeld,
            RecoveryWindowsScratchNameIdentityProbe.Evaluate(Stable() with
            {
                NamedSecond = Id(8)
            }));
    }

    [TestMethod]
    public void ChangedHeldIdRejects()
    {
        Assert.AreEqual(RecoveryWindowsScratchNameFinding.NameDoesNotMatchHeld,
            RecoveryWindowsScratchNameIdentityProbe.Evaluate(Stable() with
            {
                HeldAfter = Id(99)
            }));
    }

    [TestMethod]
    public void ReparseFlagsRejectEvenWhenIdsMatch()
    {
        Assert.AreEqual(RecoveryWindowsScratchNameFinding.ReparseOrWrongType,
            RecoveryWindowsScratchNameIdentityProbe.Evaluate(Stable() with
            {
                FirstReparse = true
            }));
        Assert.AreEqual(RecoveryWindowsScratchNameFinding.ReparseOrWrongType,
            RecoveryWindowsScratchNameIdentityProbe.Evaluate(Stable() with
            {
                SecondReparse = true
            }));
    }

    [TestMethod]
    public void WrongVolumeAndParentAliasReject()
    {
        Assert.AreEqual(RecoveryWindowsScratchNameFinding.CrossVolumeOrAlias,
            RecoveryWindowsScratchNameIdentityProbe.Evaluate(Stable() with
            {
                NamedFirst = Id(2, volume: 999)
            }));
        Assert.AreEqual(RecoveryWindowsScratchNameFinding.CrossVolumeOrAlias,
            RecoveryWindowsScratchNameIdentityProbe.Evaluate(Stable() with
            {
                HeldBefore = Id(1), HeldAfter = Id(1),
                NamedFirst = Id(1), NamedSecond = Id(1)
            }));
    }

    [TestMethod]
    public void IncompleteOrLinuxIdReject()
    {
        Assert.AreEqual(RecoveryWindowsScratchNameFinding.IncompleteEvidence,
            RecoveryWindowsScratchNameIdentityProbe.Evaluate(default));
        Assert.AreEqual(RecoveryWindowsScratchNameFinding.IncompleteEvidence,
            RecoveryWindowsScratchNameIdentityProbe.Evaluate(Stable() with
            {
                NamedSecond = new RecoveryNativeFileId(
                    RecoveryNativePlatform.Linux, 55, 2, 0)
            }));
    }

    [TestMethod]
    public void NtSharingViolationIsExplicitUnavailableNotSuccess()
    {
        Assert.AreEqual(RecoveryWindowsScratchNameOpenFinding.ExclusiveShareDenied,
            RecoveryWindowsScratchNameIdentityProbe.ClassifyNtOpenStatus(
                unchecked((int)0xC0000043)));
        Assert.IsFalse(RecoveryWindowsScratchNameIdentityProbe.CreatorSharePolicyAllowsReopen);
    }

    [TestMethod]
    public void UnknownNtStatusIsNotSuccessfulObservation()
    {
        Assert.AreEqual(RecoveryWindowsScratchNameOpenFinding.OpenFailed,
            RecoveryWindowsScratchNameIdentityProbe.ClassifyNtOpenStatus(
                unchecked((int)0xC0000022)));
        Assert.AreEqual(RecoveryWindowsScratchNameOpenFinding.OpenSucceededUntrusted,
            RecoveryWindowsScratchNameIdentityProbe.ClassifyNtOpenStatus(0));
    }

    [TestMethod]
    public void BasenameValidationRunsBeforeWindowsHandleInspection()
    {
        using var invalid = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryWindowsScratchNameIdentityProbe.ObserveExisting(
                invalid, invalid, "../not-a-journal"));
    }

    [TestMethod]
    public void InvalidHandleCannotReachNtOpenFile()
    {
        using var invalid = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            RecoveryWindowsScratchNameIdentityProbe.ObserveExisting(
                invalid, invalid, Guid.NewGuid().ToString("N") + ".gdjournal.tmp"));
    }

    [TestMethod]
    public void ObservationsNeverAllowNameBasedMutation()
    {
        var evidence = Stable();
        Assert.IsFalse(evidence.CanRestore);
        Assert.IsFalse(evidence.CanDelete);
        Assert.IsFalse(evidence.CanPublish);
        Assert.IsFalse(evidence.HasTrustedAncestry);
        Assert.IsFalse(RecoveryWindowsScratchNameIdentityProbe.CanAuthorizePublication);
    }
}
