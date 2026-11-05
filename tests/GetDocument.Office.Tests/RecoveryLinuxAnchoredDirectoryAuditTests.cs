using System.Text;
using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27bu: synthetic audit contracts, not Astra kernel qualification.
/// </summary>
[TestClass]
public sealed class RecoveryLinuxAnchoredDirectoryAuditTests
{
    [TestMethod]
    public void AbsolutePathIsSplitIntoExactComponentsWithoutNormalizingAwayTraversal()
    {
        string[] names = RecoveryLinuxAnchoredDirectoryAudit.ValidateAbsolutePath(
            "/home/operator/GET-Document/recovery-volume-coordinator");
        CollectionAssert.AreEqual(new[]
        {
            "home", "operator", "GET-Document", "recovery-volume-coordinator"
        }, names);
    }

    [TestMethod]
    public void TraversalRepeatedSeparatorAndNonAbsolutePathsAreDenied()
    {
        foreach (string path in new[]
        {
            "", "/", ".", "..", "relative/path", "/a/../b", "/a/./b",
            "/a//b", "/a/", "//a", "/a\\b", "/a\0b"
        })
            Assert.ThrowsExactly<ArgumentException>(() =>
                RecoveryLinuxAnchoredDirectoryAudit.ValidateAbsolutePath(path));
    }

    [TestMethod]
    public void UnboundedComponentsOrPathDepthAreRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryLinuxAnchoredDirectoryAudit.ValidateAbsolutePath(
                "/" + new string('x', 256)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryLinuxAnchoredDirectoryAudit.ValidateAbsolutePath(
                "/" + string.Join("/", Enumerable.Repeat("item", 33))));
    }

    [TestMethod]
    public void SystemOwnedNonWritableAncestorsAndPrivateLeafPassObservationOnly()
    {
        RecoveryLinuxParentEvidence root = Synthetic(0, 0x41ED);
        RecoveryLinuxParentEvidence home = Synthetic(1000, 0x41C0);
        RecoveryLinuxAnchoredDirectoryAudit.RequireDirectoryPolicy(
            root, effectiveUid: 1000, isFinal: false);
        RecoveryLinuxAnchoredDirectoryAudit.RequireDirectoryPolicy(
            home, effectiveUid: 1000, isFinal: true);
    }

    [TestMethod]
    public void WorldOrGroupWritableAncestorsAndForeignOwnerAreRejected()
    {
        foreach (ushort mode in new ushort[] { 0x43FF, 0x41F8, 0x41D2 })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                RecoveryLinuxAnchoredDirectoryAudit.RequireDirectoryPolicy(
                    Synthetic(0, mode), effectiveUid: 1000, isFinal: false));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryLinuxAnchoredDirectoryAudit.RequireDirectoryPolicy(
                Synthetic(2000, 0x41C0), effectiveUid: 1000, isFinal: false));
    }

    [TestMethod]
    public void FinalDirectoryMustBeOwnerOnlyAndSameEffectiveUid()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryLinuxAnchoredDirectoryAudit.RequireDirectoryPolicy(
                Synthetic(0, 0x41C0), effectiveUid: 1000, isFinal: true));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryLinuxAnchoredDirectoryAudit.RequireDirectoryPolicy(
                Synthetic(1000, 0x41ED), effectiveUid: 1000, isFinal: true));
    }

    [TestMethod]
    public void XattrListReportsAclAndSecurityLabelsWithoutAssumingSafety()
    {
        byte[] raw = Encoding.UTF8.GetBytes(
            "system.posix_acl_access\0security.selinux\0user.comment\0");
        RecoveryLinuxXattrEvidence result =
            RecoveryLinuxAnchoredDirectoryAudit.ParseXattrNames(raw, raw.Length);
        Assert.AreEqual(3, result.NameCount);
        Assert.IsTrue(result.PosixAclPresent);
        Assert.IsTrue(result.SecurityAttributePresent);
        Assert.IsTrue(result.OtherUnreviewedAttributesPresent);
        Assert.IsFalse(result.CanAuthorizeCapture);
    }

    [TestMethod]
    public void EmptyXattrListStillCannotAuthorizeAnyFileOperation()
    {
        RecoveryLinuxXattrEvidence result =
            RecoveryLinuxAnchoredDirectoryAudit.ParseXattrNames(
                Array.Empty<byte>(), 0);
        Assert.AreEqual(0, result.NameCount);
        Assert.IsFalse(result.CanAuthorizeCapture);
    }

    [TestMethod]
    public void MalformedOrOversizedXattrListsAreDenied()
    {
        foreach (byte[] bytes in new[]
        {
            Encoding.ASCII.GetBytes("user.name"),
            Encoding.ASCII.GetBytes("\0"),
            Encoding.ASCII.GetBytes("user.one\0user.one\0"),
            Enumerable.Repeat((byte)'x', 8193).ToArray()
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                RecoveryLinuxAnchoredDirectoryAudit.ParseXattrNames(
                    bytes, bytes.Length));
    }

    [TestMethod]
    public void MountAliasMismatchAndChangingAncestorEvidenceAreDenied()
    {
        RecoveryLinuxParentEvidence first = Synthetic(1000, 0x41C0);
        var differentMount = first with { MountId = first.MountId + 1 };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryLinuxAnchoredDirectoryAudit.RequireStableChain(
                first, differentMount, first));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryLinuxAnchoredDirectoryAudit.RequireStableChain(
                first, first with { OwnerUid = 2000 }, first));
    }

    [TestMethod]
    public void NoLiveInspectionIsAllowedFromUnsupportedOrInvalidPath()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryLinuxAnchoredDirectoryAudit.InspectExisting("/home/../tmp"));
    }

    private static RecoveryLinuxParentEvidence Synthetic(uint uid, ushort mode) =>
        new(new RecoveryNativeFileId(RecoveryNativePlatform.Linux, 0x100, 42, 0),
            uid, mode, 77);
}
