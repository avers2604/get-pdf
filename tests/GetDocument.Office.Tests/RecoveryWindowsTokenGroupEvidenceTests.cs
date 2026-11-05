using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27bt. Synthetic Windows token-group attribute evidence.
/// These tests do not prove AccessCheck, ACL ancestry or token privileges.
/// </summary>
[TestClass]
public sealed class RecoveryWindowsTokenGroupEvidenceTests
{
    [TestMethod]
    public void EnabledGroupIsConsideredForAllow()
    {
        Assert.AreEqual(RecoveryWindowsTokenGroupState.Enabled,
            RecoveryWindowsTokenGroupProbe.ClassifyAttributes(0x00000004));
        Assert.AreEqual(RecoveryWindowsTokenGroupState.Enabled,
            RecoveryWindowsTokenGroupProbe.ClassifyAttributes(0x00000005));
    }

    [TestMethod]
    public void DenyOnlyGroupCannotQualifyAnAllowAce()
    {
        Assert.AreEqual(RecoveryWindowsTokenGroupState.DenyOnly,
            RecoveryWindowsTokenGroupProbe.ClassifyAttributes(0x00000010));
        Assert.AreEqual(RecoveryWindowsTokenAceFinding.DenyOnlyAllowIgnored,
            RecoveryWindowsTokenGroupProbe.CompareAllow(
                new RecoveryWindowsAclAce(0, 0x00000002, "S-1-5-11"),
                new[] { new RecoveryWindowsTokenGroup("S-1-5-11", 0x10) }));
    }

    [TestMethod]
    public void DisabledGroupCannotQualifyAnAllowAce()
    {
        Assert.AreEqual(RecoveryWindowsTokenGroupState.Disabled,
            RecoveryWindowsTokenGroupProbe.ClassifyAttributes(0));
        Assert.AreEqual(RecoveryWindowsTokenAceFinding.DisabledGroupAllowIgnored,
            RecoveryWindowsTokenGroupProbe.CompareAllow(
                new RecoveryWindowsAclAce(0, 0x00000040, "S-1-5-32-545"),
                new[] { new RecoveryWindowsTokenGroup("S-1-5-32-545", 0) }));
    }

    [TestMethod]
    public void InconsistentOrIntegrityGroupIsNotAssumedEnabled()
    {
        foreach (uint flags in new uint[] { 0x14, 0x24, 0x40, 0x80 })
            Assert.AreEqual(RecoveryWindowsTokenGroupState.Unreviewed,
                RecoveryWindowsTokenGroupProbe.ClassifyAttributes(flags));
    }

    [TestMethod]
    public void EnabledBroadGroupWriteIsPotentialExposureNotPermission()
    {
        var groups = new[] { new RecoveryWindowsTokenGroup("S-1-5-11", 0x4) };
        var finding = RecoveryWindowsTokenGroupProbe.CompareAllow(
            new RecoveryWindowsAclAce(0, 0x40000000, "S-1-5-11"), groups);
        Assert.AreEqual(RecoveryWindowsTokenAceFinding.EnabledGroupWriteAllowObserved,
            finding);
        var snapshot = RecoveryWindowsTokenGroupProbe.Summarize(groups);
        Assert.AreEqual(1, snapshot.EnabledCount);
        Assert.IsFalse(snapshot.CanAuthorizeCapture);
    }

    [TestMethod]
    public void InheritOnlyAceDoesNotApplyToCurrentDirectoryButCannotProveSafety()
    {
        var finding = RecoveryWindowsTokenGroupProbe.CompareAllow(
            new RecoveryWindowsAclAce(0, 0x2, "S-1-5-11", 0x08),
            new[] { new RecoveryWindowsTokenGroup("S-1-5-11", 0x4) });
        Assert.AreEqual(RecoveryWindowsTokenAceFinding.InheritOnlyNotApplicableToObject,
            finding);
        Assert.IsFalse(RecoveryWindowsTokenGroupProbe.Summarize(
            new[] { new RecoveryWindowsTokenGroup("S-1-5-11", 0x4) })
            .CanAuthorizeCapture);
    }

    [TestMethod]
    public void UnmatchedOrUnknownTrusteeRemainsUnresolved()
    {
        Assert.AreEqual(RecoveryWindowsTokenAceFinding.UnresolvedTokenCoverage,
            RecoveryWindowsTokenGroupProbe.CompareAllow(
                new RecoveryWindowsAclAce(0, 0x2, "S-1-5-21-1-2-3-1000"),
                new[] { new RecoveryWindowsTokenGroup("S-1-5-11", 0x4) }));
        Assert.AreEqual(RecoveryWindowsTokenAceFinding.UnresolvedTokenCoverage,
            RecoveryWindowsTokenGroupProbe.CompareAllow(
                new RecoveryWindowsAclAce(0, 0x2, null),
                Array.Empty<RecoveryWindowsTokenGroup>()));
    }

    [TestMethod]
    public void ReadOnlyOrDenyAceCannotBeUsedToGrantWriteAccess()
    {
        var groups = new[] { new RecoveryWindowsTokenGroup("S-1-5-11", 0x4) };
        Assert.AreEqual(RecoveryWindowsTokenAceFinding.NoRelevantAllowWrite,
            RecoveryWindowsTokenGroupProbe.CompareAllow(
                new RecoveryWindowsAclAce(0, 0x00120089, "S-1-5-11"), groups));
        Assert.AreEqual(RecoveryWindowsTokenAceFinding.NoRelevantAllowWrite,
            RecoveryWindowsTokenGroupProbe.CompareAllow(
                new RecoveryWindowsAclAce(1, 0x2, "S-1-5-11"), groups));
    }

    [TestMethod]
    public void DuplicateOrOversizedGroupEvidenceFailsClosed()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryWindowsTokenGroupProbe.Summarize(new[]
            {
                new RecoveryWindowsTokenGroup("S-1-5-11", 0x4),
                new RecoveryWindowsTokenGroup("S-1-5-11", 0x10)
            }));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryWindowsTokenGroupProbe.Summarize(
                Enumerable.Repeat(new RecoveryWindowsTokenGroup("S-1-5-11", 0x4), 257)
                    .ToArray()));
    }

    [TestMethod]
    public void InvalidOrClosedTokenHandleIsRejectedBeforeNativeCall()
    {
        using var invalid = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            RecoveryWindowsTokenGroupProbe.ObserveExistingToken(invalid));
    }
}
