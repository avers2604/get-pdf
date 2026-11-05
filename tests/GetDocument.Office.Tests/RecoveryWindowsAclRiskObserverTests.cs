using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27bs: conservative READ-ONLY ACE risk evidence, not access approval.
/// Native Windows E2E and effective-token authorization remain separate gates.
/// </summary>
[TestClass]
public sealed class RecoveryWindowsAclRiskObserverTests
{
    [TestMethod]
    public void BroadWellKnownTrusteesWithWriteRightsAreFlagged()
    {
        foreach (string sid in new[] { "S-1-1-0", "S-1-5-11", "S-1-5-32-545",
                                       "S-1-5-32-546", "S-1-5-2" })
        foreach (uint right in new[] { 0x2u, 0x40u, 0x10000u, 0x40000u,
                                        0x80000u, 0x40000000u, 0x10000000u })
        {
            var result = RecoveryWindowsAclRiskObserver.ClassifyAce(
                new RecoveryWindowsAclAce(0, right, sid));
            Assert.AreEqual(RecoveryWindowsAceRisk.BroadGroupWriteAllow, result,
                $"Failed for SID={sid}, right={right:X}");
        }
    }

    [TestMethod]
    public void ReadOnlyBroadGroupDoesNotMakeDaclTrusted()
    {
        var evidence = RecoveryWindowsAclRiskObserver.Summarize(new[]
        {
            new RecoveryWindowsAclAce(0, 0x120089, "S-1-1-0")
        });
        Assert.AreEqual(1, evidence.AceCount);
        Assert.AreEqual(0, evidence.BroadWriteAllowCount);
        Assert.AreEqual(0, evidence.UnreviewedAceCount);
        Assert.IsFalse(evidence.CanAuthorizeCapture);
    }

    [TestMethod]
    public void OwnerWriteAceIsNotMislabelledAsBroadGroupButStillCannotAuthorize()
    {
        var evidence = RecoveryWindowsAclRiskObserver.Summarize(new[]
        {
            new RecoveryWindowsAclAce(0, 0x2, "S-1-5-21-100-200-300-1001")
        });
        Assert.AreEqual(0, evidence.BroadWriteAllowCount);
        Assert.IsFalse(evidence.CanAuthorizeCapture);
    }

    [TestMethod]
    public void UnsupportedCallbackObjectOrMalformedSidStaysUnreviewed()
    {
        var evidence = RecoveryWindowsAclRiskObserver.Summarize(new[]
        {
            new RecoveryWindowsAclAce(9, 0x2, "S-1-1-0"),
            new RecoveryWindowsAclAce(5, 0x2, "S-1-1-0"),
            new RecoveryWindowsAclAce(0, 0x2, null)
        });
        Assert.AreEqual(3, evidence.UnreviewedAceCount);
        Assert.IsFalse(evidence.CanAuthorizeCapture);
    }

    [TestMethod]
    public void DenyAceDoesNotEraseAnAllowRiskWithoutEffectiveRightsAnalysis()
    {
        var evidence = RecoveryWindowsAclRiskObserver.Summarize(new[]
        {
            new RecoveryWindowsAclAce(1, 0x2, "S-1-1-0"),
            new RecoveryWindowsAclAce(0, 0x2, "S-1-1-0")
        });
        Assert.AreEqual(1, evidence.BroadWriteAllowCount);
        Assert.IsFalse(evidence.CanAuthorizeCapture);
    }

    [TestMethod]
    public void EmptyDaclHasNoPositiveAuthorizationAndOversizedListIsRejected()
    {
        var empty = RecoveryWindowsAclRiskObserver.Summarize(
            Array.Empty<RecoveryWindowsAclAce>());
        Assert.AreEqual(0, empty.AceCount);
        Assert.IsFalse(empty.CanAuthorizeCapture);
        var tooMany = Enumerable.Repeat(
            new RecoveryWindowsAclAce(0, 0u, "S-1-1-0"), 257).ToArray();
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryWindowsAclRiskObserver.Summarize(tooMany));
    }

    [TestMethod]
    public void ReadingExistingWindowsHandleNeverApprovesCaptureOrMutatesFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        string path = Path.Combine(Path.GetTempPath(),
            "getdoc-bs-acl-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "UNCHANGED");
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var evidence = RecoveryWindowsAclRiskObserver.ObserveExistingHandle(handle);
            Assert.IsTrue(evidence.AceCount >= 0);
            Assert.IsFalse(evidence.CanAuthorizeCapture);
            Assert.AreEqual("UNCHANGED", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
