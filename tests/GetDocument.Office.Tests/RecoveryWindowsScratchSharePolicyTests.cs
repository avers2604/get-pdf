using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27ce block 1: only a separately opted-in lab read-sharing
/// variant may permit CD's read-only NtOpenFile while the CC writer is held.
/// </summary>
[TestClass]
public sealed class RecoveryWindowsScratchSharePolicyTests
{
    private static string Name() => Guid.NewGuid().ToString("N") + ".gdjournal.tmp";

    [TestMethod]
    public void ExistingExclusiveCreationRemainsDefault()
    {
        Assert.AreEqual(0u, RecoveryWindowsJournalScratchFactory.GetLabShareMask(
            RecoveryWindowsScratchShareMode.Exclusive));
        Assert.AreEqual(RecoveryWindowsScratchShareMode.Exclusive,
            RecoveryWindowsScratchSharePolicy.DefaultMode);
    }

    [TestMethod]
    public void ExplicitReadObserversShareOnlyReadAndNotDeleteOrWrite()
    {
        uint flags = RecoveryWindowsJournalScratchFactory.GetLabShareMask(
            RecoveryWindowsScratchShareMode.ReadOnlyObservers);
        Assert.AreEqual(1u, flags); // FILE_SHARE_READ
        Assert.AreEqual(0u, flags & (2u | 4u)); // no WRITE or DELETE sharing
        Assert.IsFalse(RecoveryWindowsScratchSharePolicy.CanAuthorizePublication);
    }

    [TestMethod]
    public void InvalidSharePolicyIsRefusedBeforeAnyNativeInvocation()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            RecoveryWindowsJournalScratchFactory.GetLabShareMask(
                (RecoveryWindowsScratchShareMode)128));
    }

    [TestMethod]
    public void ReadSharedOptInIsSeparateFromOriginalLabFlag()
    {
        Assert.IsFalse(RecoveryWindowsScratchSharePolicy.ProductionReadSharingEnabled);
        Assert.IsFalse(RecoveryWindowsScratchSharePolicy.HasVerifiedNtfsSharingSemantics);
        Assert.IsFalse(RecoveryWindowsScratchSharePolicy.HasVerifiedSmbSharingSemantics);
    }

    [TestMethod]
    public void NoOptInReadSharingFailsBeforePlatformCalls()
    {
        if (Environment.GetEnvironmentVariable("GD31J27CC_NATIVE_LAB") != "1" ||
            Environment.GetEnvironmentVariable("GD31J27CE_READ_SHARE_LAB") == "1")
            return;

        using var synthetic = new SafeFileHandle((IntPtr)0x654321, ownsHandle: false);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            RecoveryWindowsJournalScratchFactory.CreateOnHeldParentForLab(
                synthetic, Name(), "S-1-5-21-1-2-3-4",
                shareMode: RecoveryWindowsScratchShareMode.ReadOnlyObservers));
    }

    [TestMethod]
    public void ReadSharedModeNeverGrantsRecoveryOrCleanup()
    {
        Assert.IsFalse(RecoveryWindowsJournalScratchFactory.CanAuthorizePublication);
        Assert.IsFalse(RecoveryWindowsJournalScratchFactory.CanAuthorizeCleanup);
        Assert.IsFalse(RecoveryWindowsJournalScratchFactory.ProductionCreateEnabled);
        Assert.IsFalse(RecoveryWindowsScratchSharePolicy.CanAuthorizeCleanup);
    }

    [TestMethod]
    public void NtSharingViolationRemainsExplicitFailureUntilOsQualified()
    {
        Assert.AreEqual(RecoveryWindowsScratchNameOpenFinding.ExclusiveShareDenied,
            RecoveryWindowsScratchNameIdentityProbe.ClassifyNtOpenStatus(
                unchecked((int)0xC0000043)));
        Assert.IsFalse(RecoveryWindowsScratchNameIdentityProbe.CreatorSharePolicyAllowsReopen);
    }
}
