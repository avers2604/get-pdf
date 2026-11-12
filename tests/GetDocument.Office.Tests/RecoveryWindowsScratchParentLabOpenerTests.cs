using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>GD-31J.27ce block 2: lab-only Windows parent with requested FILE_ADD_FILE.</summary>
[TestClass]
public sealed class RecoveryWindowsScratchParentLabOpenerTests
{
    private static RecoveryNativeFileId Id(ulong id) =>
        new(RecoveryNativePlatform.Windows, 22, id, 7);
    private const string Sid = "S-1-5-21-1-2-3-4";

    [TestMethod]
    public void RequestedAccessIncludesAddFileTraverseReadControlAndSync()
    {
        uint mask = RecoveryWindowsScratchParentLabOpener.RequiredAccessMask;
        Assert.AreEqual(0x00000002u, mask & 0x00000002u); // FILE_ADD_FILE
        Assert.AreEqual(0x00000020u, mask & 0x00000020u); // FILE_TRAVERSE
        Assert.AreEqual(0x00000080u, mask & 0x00000080u); // FILE_READ_ATTRIBUTES
        Assert.AreEqual(0x00020000u, mask & 0x00020000u); // READ_CONTROL
        Assert.AreEqual(0x00100000u, mask & 0x00100000u); // SYNCHRONIZE
        Assert.AreEqual(0u, mask & 0x00010000u); // no DELETE
        Assert.AreEqual(0u, mask & 0x00040000u); // no WRITE_DAC
    }

    [TestMethod]
    public void OwnerAndAclObservationCannotGrantTrustedAncestry()
    {
        var parent = new RecoveryWindowsParentEvidence(Sid, true, Id(1));
        var acl = RecoveryWindowsAclRiskObserver.Summarize(
            new[] { new RecoveryWindowsAclAce(0, 0x00020000u, Sid) });
        Assert.AreEqual(RecoveryWindowsScratchParentFinding.ObservedButNotTrusted,
            RecoveryWindowsScratchParentLabOpener.Evaluate(parent, acl, Sid));
        Assert.IsFalse(RecoveryWindowsScratchParentLabOpener.CanAuthorizeProductionRoot);
        Assert.IsFalse(RecoveryWindowsScratchParentLabOpener.HasVerifiedAncestorChain);
    }

    [TestMethod]
    public void NullDaclOrWrongOwnerRefused()
    {
        var acl = RecoveryWindowsAclRiskObserver.Summarize(
            new[] { new RecoveryWindowsAclAce(0, 0x00020000u, Sid) });
        Assert.AreEqual(RecoveryWindowsScratchParentFinding.InvalidOwnerOrDacl,
            RecoveryWindowsScratchParentLabOpener.Evaluate(
                new RecoveryWindowsParentEvidence(Sid, false, Id(1)), acl, Sid));
        Assert.AreEqual(RecoveryWindowsScratchParentFinding.InvalidOwnerOrDacl,
            RecoveryWindowsScratchParentLabOpener.Evaluate(
                new RecoveryWindowsParentEvidence(Sid, true, Id(1)), acl,
                "S-1-5-21-1-2-3-5"));
    }

    [TestMethod]
    public void BroadAclAndIncompleteIdentityRefused()
    {
        var acl = RecoveryWindowsAclRiskObserver.Summarize(
            new[] { new RecoveryWindowsAclAce(0, 0x00000002u, "S-1-1-0") });
        Assert.AreEqual(RecoveryWindowsScratchParentFinding.UnreviewedAcl,
            RecoveryWindowsScratchParentLabOpener.Evaluate(
                new RecoveryWindowsParentEvidence(Sid, true, Id(1)), acl, Sid));
        Assert.AreEqual(RecoveryWindowsScratchParentFinding.Incomplete,
            RecoveryWindowsScratchParentLabOpener.Evaluate(
                new RecoveryWindowsParentEvidence(Sid, true, default), acl, Sid));
    }

    [TestMethod]
    public void RelativeAndUncLocationsRejectedBeforeOsCalls()
    {
        foreach (string path in new[] { "relative", "../outside", @"\\server\share\folder" })
            Assert.ThrowsExactly<ArgumentException>(() =>
                RecoveryWindowsScratchParentLabOpener.ValidateLocalAbsoluteLabPath(path));
    }

    [TestMethod]
    public void EmptyOrRootPathRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryWindowsScratchParentLabOpener.ValidateLocalAbsoluteLabPath(""));
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryWindowsScratchParentLabOpener.ValidateLocalAbsoluteLabPath(null!));
    }

    [TestMethod]
    public void MissingLabAuthorizationDenied()
    {
        Assert.IsFalse(RecoveryWindowsScratchParentLabOpener.HasVerifiedEffectiveAccess);
        Assert.IsFalse(RecoveryWindowsScratchParentLabOpener.CanAuthorizeCleanup);
        Assert.IsFalse(RecoveryWindowsScratchParentLabOpener.ProductionOpenerEnabled);
    }

    [TestMethod]
    public void DiagnosticLeaseNeverDeletesAnything()
    {
        Assert.IsFalse(RecoveryWindowsScratchParentLabOpener.CanAuthorizeDeletion);
        Assert.IsFalse(RecoveryWindowsScratchParentLabOpener.CanAuthorizePublication);
    }
}
