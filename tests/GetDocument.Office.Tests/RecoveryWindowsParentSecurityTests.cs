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

    [TestMethod]
    public void WindowsPinnedDirectoryRequiresAbsolutePath()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryWindowsPinnedParentProbe.OpenForDiagnostics("relative-dir"));
    }

    [TestMethod]
    public void WindowsPinnedDirectoryReportsSameHandleIdentityWithoutMutation()
    {
        if (!OperatingSystem.IsWindows()) return;
        string dir = Path.Combine(Path.GetTempPath(),
            "getdoc-bq-win-parent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var pinned = RecoveryWindowsPinnedParentProbe.OpenForDiagnostics(dir);
            Assert.IsTrue(pinned.Security.FileId.IsComplete);
            Assert.IsTrue(pinned.Security.OwnerSid.StartsWith("S-1-",
                StringComparison.Ordinal));
            // ACL summary is a second read of the SAME held directory handle:
            // it cannot establish effective access or authorize capture.
            Assert.IsTrue(pinned.AclRisk.AceCount >= 0);
            Assert.IsFalse(pinned.AclRisk.CanAuthorizeCapture);
            Assert.AreEqual(pinned.Security.FileId,
                RecoveryNativeHandleIdProbe.ReadFromExistingHandle(pinned.Handle));
            Assert.IsFalse(RecoveryWindowsParentSecurityGate.CanAuthorizeCapture(
                RecoveryWindowsParentSecurityGate.Evaluate(
                    pinned.Security, pinned.Security.OwnerSid)));
        }
        finally { Directory.Delete(dir); }
    }

    [TestMethod]
    public void WindowsPinnedDirectoryRejectsFinalReparsePoint()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(),
            "getdoc-bq-win-link-" + Guid.NewGuid().ToString("N"));
        string real = Path.Combine(root, "real");
        string link = Path.Combine(root, "linked");
        Directory.CreateDirectory(real);
        try
        {
            try { Directory.CreateSymbolicLink(link, real); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or
                                       IOException or PlatformNotSupportedException)
            { return; }

            Assert.ThrowsExactly<InvalidDataException>(() =>
                RecoveryWindowsPinnedParentProbe.OpenForDiagnostics(link));
            Assert.IsTrue(Directory.Exists(real));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static RecoveryNativeFileId GoodId() =>
        new(RecoveryNativePlatform.Windows, 44, 123, 456);
}
