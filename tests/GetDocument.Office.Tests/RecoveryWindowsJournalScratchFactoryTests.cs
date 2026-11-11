using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class RecoveryWindowsJournalScratchFactoryTests
{
    private const string Owner = "S-1-5-21-100-200-300-400";
    private static string Name() => "1234567890abcdef1234567890abcdef.gdjournal.tmp";

    private static RecoveryNativeFileId Id(ulong value, ulong volume = 44) =>
        new(RecoveryNativePlatform.Windows, volume, value, 9);

    private static RecoveryWindowsParentEvidence Parent(
        string owner = Owner, bool dacl = true, ulong fileId = 1) =>
        new(owner, dacl, Id(fileId));

    private static RecoveryWindowsAclRiskEvidence Acl(
        int broad = 0, int unknown = 0, int denied = 0) =>
        new(broad + unknown + denied, broad, unknown, denied,
            Array.Empty<RecoveryWindowsAclAce>());

    [TestMethod]
    public void StrictLowercaseScratchBasenameIsAccepted()
    {
        Assert.AreEqual(Name(),
            RecoveryWindowsJournalScratchFactory.ValidateName(Name()));
    }

    [TestMethod]
    public void PathsDevicesAndSeparatorsAreNotScratchBasenames()
    {
        foreach (string s in new[]
        {
            "../" + Name(), "C:\\" + Name(), "\\??\\" + Name(),
            "sub\\" + Name(), "sub/" + Name(), "\\\\server\\share\\" + Name(),
            Name() + "\0", Name() + ":data"
        })
            Assert.ThrowsExactly<ArgumentException>(() =>
                RecoveryWindowsJournalScratchFactory.ValidateName(s));
    }

    [TestMethod]
    public void RejectsCaseVariantAlternateExtensionAndEmptyGuid()
    {
        foreach (string s in new[]
        {
            Name().ToUpperInvariant(), Name()[..32] + ".pdf",
            new string('0', 32) + ".gdjournal.tmp",
            Name() + ".bak", new string('g', 32) + ".gdjournal.tmp"
        })
            Assert.ThrowsExactly<ArgumentException>(() =>
                RecoveryWindowsJournalScratchFactory.ValidateName(s));
    }

    [TestMethod]
    public void DeniesBroadUnknownOrDenyAceAsEffectivePolicyProof()
    {
        foreach (var acl in new[] { Acl(broad: 1), Acl(unknown: 1), Acl(denied: 1) })
            Assert.AreEqual(RecoveryWindowsScratchParentFinding.UnreviewedAcl,
                RecoveryWindowsJournalScratchFactory.EvaluateParent(
                    Parent(), acl, Owner));
    }

    [TestMethod]
    public void NullDaclAndMismatchedOwnerAreRejected()
    {
        Assert.AreEqual(RecoveryWindowsScratchParentFinding.InvalidOwnerOrDacl,
            RecoveryWindowsJournalScratchFactory.EvaluateParent(
                Parent(dacl: false), Acl(), Owner));
        Assert.AreEqual(RecoveryWindowsScratchParentFinding.InvalidOwnerOrDacl,
            RecoveryWindowsJournalScratchFactory.EvaluateParent(
                Parent(owner: "S-1-5-21-100-200-300-401"), Acl(), Owner));
    }

    [TestMethod]
    public void CompleteInputsRemainObservedOnlyAndNotAuthority()
    {
        var result = RecoveryWindowsJournalScratchFactory.EvaluateParent(
            Parent(), Acl(), Owner);
        Assert.AreEqual(RecoveryWindowsScratchParentFinding.ObservedButNotTrusted,
            result);
        Assert.IsFalse(RecoveryWindowsJournalScratchFactory.ProductionCreateEnabled);
        Assert.IsFalse(RecoveryWindowsJournalScratchFactory.CanAuthorizeCleanup);
        Assert.IsFalse(RecoveryWindowsJournalScratchFactory.CanAuthorizePublication);
        Assert.IsFalse(RecoveryWindowsJournalScratchFactory.HasVerifiedEffectiveAccess);
    }

    [TestMethod]
    public void ChangedParentAndCrossVolumeFileIdAreRejected()
    {
        Assert.AreEqual(RecoveryWindowsScratchFileFinding.ParentChanged,
            RecoveryWindowsJournalScratchFactory.EvaluateFileIdentity(
                Id(1), Id(99), Id(2)));
        Assert.AreEqual(RecoveryWindowsScratchFileFinding.CrossVolumeOrAlias,
            RecoveryWindowsJournalScratchFactory.EvaluateFileIdentity(
                Id(1), Id(1), Id(2, 88)));
        Assert.AreEqual(RecoveryWindowsScratchFileFinding.CrossVolumeOrAlias,
            RecoveryWindowsJournalScratchFactory.EvaluateFileIdentity(
                Id(1), Id(1), Id(1)));
    }

    [TestMethod]
    public void NonWindowsAndIncompleteFileIdsCannotPass()
    {
        var linux = new RecoveryNativeFileId(
            RecoveryNativePlatform.Linux, 44, 1, 0);
        Assert.AreEqual(RecoveryWindowsScratchFileFinding.Incomplete,
            RecoveryWindowsJournalScratchFactory.EvaluateFileIdentity(
                linux, linux, Id(2)));
        Assert.AreEqual(RecoveryWindowsScratchFileFinding.Incomplete,
            RecoveryWindowsJournalScratchFactory.EvaluateFileIdentity(
                default, default, default));
    }

    [TestMethod]
    public void StableIdsAreOnlyObservation()
    {
        Assert.AreEqual(RecoveryWindowsScratchFileFinding.ObservedButNotTrusted,
            RecoveryWindowsJournalScratchFactory.EvaluateFileIdentity(
                Id(1), Id(1), Id(2)));
    }

    [TestMethod]
    public void NoPathBasedFallbackOrAutomaticallyTrustedCommit()
    {
        Assert.IsFalse(RecoveryWindowsJournalScratchFactory.CanCreateByPath);
        Assert.IsFalse(RecoveryWindowsJournalScratchFactory.HasTrustedAncestry);
        Assert.IsFalse(RecoveryWindowsJournalScratchFactory.HasVerifiedReparseIsolation);
        Assert.IsFalse(RecoveryWindowsJournalScratchFactory.HasVerifiedDirectoryDurability);
    }

    [TestMethod]
    public void InvalidNameDeniedBeforeInvalidHandleOrPlatform()
    {
        using var bad = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryWindowsJournalScratchFactory.CreateOnHeldParentForLab(
                bad, "../attack", Owner));
    }

    [TestMethod]
    public void ClosedParentHandleCannotReachNativeCreate()
    {
        using var bad = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            RecoveryWindowsJournalScratchFactory.CreateOnHeldParentForLab(
                bad, Name(), Owner));
    }

    [TestMethod]
    public void MissingLabOptInDeniedBeforeWindowsNativeInvocation()
    {
        if (Environment.GetEnvironmentVariable("GD31J27CC_NATIVE_LAB") == "1")
            return;
        using var synthetic = new SafeFileHandle((nint)0x765432, ownsHandle: false);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            RecoveryWindowsJournalScratchFactory.CreateOnHeldParentForLab(
                synthetic, Name(), Owner));
    }

    [TestMethod]
    public void ReportCannotAuthorizeMutationOrOriginalDocumentChange()
    {
        var report = new RecoveryWindowsScratchObservation(
            Id(1), Id(2), Name(), Owner);
        Assert.IsFalse(report.CanAuthorizeRecovery);
        Assert.IsFalse(report.CanAuthorizeDelete);
        Assert.IsFalse(report.CanAuthorizePublish);
        Assert.IsFalse(report.HasIndependentOwnerVerification);
    }

    [TestMethod]
    public void ExplicitWindowsLabOnlyCreatesUniqueFixtureWithoutOverwrite()
    {
        if (!OperatingSystem.IsWindows() ||
            Environment.GetEnvironmentVariable("GD31J27CC_NATIVE_LAB") != "1")
            return;
        // A dedicated ACL-qualified lab folder must be supplied separately.
        // No fallback on arbitrary %TEMP%; it is often broadly writable.
        string? directory = Environment.GetEnvironmentVariable(
            "GD31J27CC_PRIVATE_LAB_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory))
            return;
        using var parent = RecoveryWindowsPinnedParentProbe.OpenForDiagnostics(
            directory);
        string name = Guid.NewGuid().ToString("N") + ".gdjournal.tmp";
        using (var scratch = RecoveryWindowsJournalScratchFactory
            .CreateOnHeldParentForLab(parent.Handle, name,
                parent.Security.OwnerSid))
        {
            Assert.AreEqual(0L, scratch.Stream.Length);
            Assert.IsFalse(scratch.CanAuthorizePublish);
            Assert.ThrowsExactly<IOException>(() =>
            {
                using var again = RecoveryWindowsJournalScratchFactory
                    .CreateOnHeldParentForLab(parent.Handle, name,
                        parent.Security.OwnerSid);
            });
        }
        // Intentionally no cleanup: the operator reviews the lab artifact.
    }
}
