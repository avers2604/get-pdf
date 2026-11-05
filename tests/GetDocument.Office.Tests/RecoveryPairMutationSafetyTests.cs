using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27bv: no path-based deletion without tested native identity-bound
/// unlink/rename. These synthetic checks are not platform acceptance.
/// </summary>
[TestClass]
public sealed class RecoveryPairMutationSafetyTests
{
    private static RecoveryNativeFileId Win(ulong id) =>
        new(RecoveryNativePlatform.Windows, 17, id, 123);

    private static RecoveryPairIdentityEvidence Good() =>
        new(Win(1), Win(2), Win(3), Win(2), Win(3),
            ParentHandleHeld: true, NamesOpenedRelativeToParent: true,
            NoFollowEnforced: true, WriterIsolationConfirmed: true,
            LockHeld: true);

    [TestMethod]
    public void PathBasedDeletionIsAlwaysDenied()
    {
        Assert.IsFalse(RecoveryPairMutationGate.CanDeleteByPath);
        Assert.IsFalse(RecoveryPairMutationGate.CanPublishByPath);
        Assert.IsFalse(RecoveryPairMutationGate.CanAuthorizeNativeMutation(Good()));
    }

    [TestMethod]
    public void MissingNativeIdentityIsRejected()
    {
        var evidence = Good() with { Parent = default };
        Assert.AreEqual(RecoveryPairMutationDecision.IncompleteNativeIdentity,
            RecoveryPairMutationGate.Evaluate(evidence));
    }

    [TestMethod]
    public void CopyOrManifestSwapAcrossCheckpointsIsRejected()
    {
        Assert.AreEqual(RecoveryPairMutationDecision.ReplacedOrChanged,
            RecoveryPairMutationGate.Evaluate(Good() with { CopyAtRecheck = Win(11) }));
        Assert.AreEqual(RecoveryPairMutationDecision.ReplacedOrChanged,
            RecoveryPairMutationGate.Evaluate(Good() with { ManifestAtRecheck = Win(13) }));
    }

    [TestMethod]
    public void CopyAndManifestMustNotBeSameFileOrCrossVolume()
    {
        Assert.AreEqual(RecoveryPairMutationDecision.AliasedPair,
            RecoveryPairMutationGate.Evaluate(Good() with { Manifest = Win(2), ManifestAtRecheck = Win(2) }));
        Assert.AreEqual(RecoveryPairMutationDecision.CrossVolume,
            RecoveryPairMutationGate.Evaluate(Good() with
            {
                Manifest = new RecoveryNativeFileId(RecoveryNativePlatform.Windows, 99, 3, 123),
                ManifestAtRecheck = new RecoveryNativeFileId(RecoveryNativePlatform.Windows, 99, 3, 123)
            }));
    }

    [TestMethod]
    public void MissingParentIsolationOrHeldLockIsRejected()
    {
        foreach (var candidate in new[]
        {
            Good() with { ParentHandleHeld = false },
            Good() with { NamesOpenedRelativeToParent = false },
            Good() with { NoFollowEnforced = false },
            Good() with { WriterIsolationConfirmed = false },
            Good() with { LockHeld = false }
        })
            Assert.AreEqual(RecoveryPairMutationDecision.MissingExclusiveOwnership,
                RecoveryPairMutationGate.Evaluate(candidate));
    }

    [TestMethod]
    public void EvenPlausibleHeldIdsAreNotWriteOrDeleteAuthority()
    {
        Assert.AreEqual(RecoveryPairMutationDecision.NeedsNativeBoundOperation,
            RecoveryPairMutationGate.Evaluate(Good()));
        Assert.IsFalse(RecoveryPairMutationGate.CanAuthorizeNativeMutation(Good()));
    }

    [TestMethod]
    public void InvalidOrDisposedHandleIsRefusedByReadOnlyObservation()
    {
        using var invalid = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            RecoveryPairMutationGate.ObserveHeldPair(invalid, invalid, invalid));
    }

    [TestMethod]
    public void NativeObservationOfThreeIndependentSyntheticFilesNeverGrantsMutation()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        string folder = Path.Combine(Path.GetTempPath(),
            "getdoc-bv-held-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string first = Path.Combine(folder, "a.bin");
            string second = Path.Combine(folder, "b.bin");
            string parentMarker = Path.Combine(folder, "parent.bin");
            File.WriteAllText(first, "FIRST");
            File.WriteAllText(second, "SECOND");
            File.WriteAllText(parentMarker, "NOT_A_TRUSTED_DIRECTORY");
            // A regular file intentionally stands in for parent: this is
            // ONLY a held-identity smoke test. It cannot attest a directory.
            using SafeFileHandle parent = File.OpenHandle(parentMarker, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using SafeFileHandle copy = File.OpenHandle(first, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using SafeFileHandle manifest = File.OpenHandle(second, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var observed = RecoveryPairMutationGate.ObserveHeldPair(
                parent, copy, manifest);
            Assert.IsFalse(RecoveryPairMutationGate.CanAuthorizeNativeMutation(observed));
            Assert.AreEqual("FIRST", File.ReadAllText(first));
            Assert.AreEqual("SECOND", File.ReadAllText(second));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void DefaultLegacyCleanupMustNeverDeleteAnUnownedNamedFile()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "getdoc-bv-no-delete-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(path, "KEEP_OWNER_FILE");
        try
        {
            // No native owner receipt supplied: a path is not authorization.
            OfficeRecoveryStore.TryDelete(path);
            Assert.AreEqual("KEEP_OWNER_FILE", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path); // test's own uniquely named fixture only
        }
    }

    [TestMethod]
    public void DisabledPruningStopsAdmissionAtBoundedCommittedManifestLimit()
    {
        static string Manifest(int i) =>
            Guid.Parse($"00000000-0000-0000-0000-{i:000000000000}").ToString("N") +
            ".json";
        RecoveryPairMutationGate.RequireCaptureBudget(
            Enumerable.Range(1, 31).Select(Manifest), maxManifests: 32);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryPairMutationGate.RequireCaptureBudget(
                Enumerable.Range(1, 32).Select(Manifest), maxManifests: 32));
        // No delete callback or actual filesystem mutation is possible.
        Assert.IsFalse(RecoveryPairMutationGate.CanDeleteByPath);
    }

    [TestMethod]
    public void CaptureBudgetRejectsUnknownOrInvalidThresholdAndDoesNotHideEnumerationErrors()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            RecoveryPairMutationGate.RequireCaptureBudget(
                Array.Empty<string>(), maxManifests: 0));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryPairMutationGate.RequireCaptureBudget(
                FailingIterator(), maxManifests: 32));
        static IEnumerable<string> FailingIterator()
        {
            yield return Guid.NewGuid().ToString("N") + ".json";
            throw new InvalidDataException("Injected enumeration failure");
        }
    }

    [TestMethod]
    public void PairIdentityEvaluationRejectsMixedPlatforms()
    {
        var other = new RecoveryNativeFileId(RecoveryNativePlatform.Linux, 17, 2, 0);
        Assert.AreEqual(RecoveryPairMutationDecision.IncompleteNativeIdentity,
            RecoveryPairMutationGate.Evaluate(Good() with
            {
                Copy = other, CopyAtRecheck = other
            }));
    }
}
