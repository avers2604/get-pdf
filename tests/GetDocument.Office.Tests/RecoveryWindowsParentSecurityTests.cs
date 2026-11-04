using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27bq source-level Windows ACL observation contracts.
/// Windows live-handle tests below require offline NTFS/ACL qualification.
/// No successful result grants delete/write/capture permissions.
/// </summary>
[TestClass]
public sealed class RecoveryWindowsParentSecurityTests
{
    [TestMethod]
    public void WrongOwnerSidIsDenied()
    {
        var finding = RecoveryWindowsParentSecurityGate.Evaluate(
            new RecoveryWindowsParentEvidence("S-1-5-21-1-1001", true, GoodId()),
            "S-1-5-21-1-1002");
        Assert.AreEqual(RecoveryWindowsParentDecision.OwnerMismatch, finding);
    }

    [TestMethod]
    public void MissingDaclIsDeniedEvenWithMatchingOwner()
    {
        var finding = RecoveryWindowsParentSecurityGate.Evaluate(
            new RecoveryWindowsParentEvidence("S-1-5-21-1-1001", false, GoodId()),
            "S-1-5-21-1-1001");
        Assert.AreEqual(RecoveryWindowsParentDecision.NullOrUnavailableDacl, finding);
    }

    [TestMethod]
    public void MatchingOwnerAndPresentDaclStillNeedsAceReview()
    {
        var finding = RecoveryWindowsParentSecurityGate.Evaluate(
            new RecoveryWindowsParentEvidence("S-1-5-21-1-1001", true, GoodId()),
            "S-1-5-21-1-1001");
        Assert.AreEqual(RecoveryWindowsParentDecision.RequiresExplicitAceAudit, finding);
        Assert.IsFalse(RecoveryWindowsParentSecurityGate.CanAuthorizeCapture(finding));
    }

    [TestMethod]
    public void EmptyOrMalformedOwnerIdentityCannotBeAttested()
    {
        foreach (string sid in new[] { "", "DOMAIN\\User", "S-1-5-21-1-1001 " })
        {
            var finding = RecoveryWindowsParentSecurityGate.Evaluate(
                new RecoveryWindowsParentEvidence(sid, true, GoodId()),
                "S-1-5-21-1-1001");
            Assert.AreEqual(RecoveryWindowsParentDecision.MissingOrInvalidOwner, finding);
        }
    }

    [TestMethod]
    public void IncompleteNativeFileIdIsDeniedBeforeOwnerEvidence()
    {
        var finding = RecoveryWindowsParentSecurityGate.Evaluate(
            new RecoveryWindowsParentEvidence("S-1-5-21-1-1001", true,
                new RecoveryNativeFileId(RecoveryNativePlatform.Windows, 0, 0, 0)),
            "S-1-5-21-1-1001");
        Assert.AreEqual(RecoveryWindowsParentDecision.IncompleteFileIdentity, finding);
    }

    [TestMethod]
    public void HandleBasedWindowsOwnerAndDaclCanBeReadWithoutWriting()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(),
            "getdoc-bq-win-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // File.OpenHandle on this temporary REGULAR file is sufficient
            // for the read-only security-descriptor observation contract.
            string file = Path.Combine(root, "read-only-probe.bin");
            File.WriteAllText(file, "KEEP_ORIGINAL");
            using var handle = File.OpenHandle(file, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            RecoveryWindowsParentEvidence observed =
                RecoveryWindowsParentSecurityProbe.ObserveExistingHandle(handle);
            Assert.IsTrue(observed.FileId.IsComplete);
            Assert.IsTrue(observed.OwnerSid.StartsWith("S-1-", StringComparison.Ordinal));
            Assert.AreEqual("KEEP_ORIGINAL", File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static RecoveryNativeFileId GoodId() =>
        new(RecoveryNativePlatform.Windows, 44, 123, 456);
}
