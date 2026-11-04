"""GD-31J.27bm — source-only contracts for cooperative capture admission.

These assertions check text. They are NOT .NET builds, MSTest, executed
multi-process tests, native lock proofs, SMB leases or crash guarantees.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
GATE = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryCaptureVolumeGate.cs"
STORE = ROOT / "src/GetDocument.Office/LibreOffice/OfficeRecoveryStore.cs"
TESTS = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryCaptureVolumeGateTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class CaptureVolumeGateSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.gate = GATE.read_text(encoding="utf-8")
        cls.store = STORE.read_text(encoding="utf-8")
        cls.tests = TESTS.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")
        cls.capture = cls.store.split(
            "public async Task<OfficeRecoveryCopy> CaptureAsync(", 1
        )[1].split("public OfficeRecoveryIncompleteInventory InspectIncomplete(", 1)[0]
        cls.acquire = cls.gate.split(
            "internal static async Task<RecoveryCaptureVolumeLease> AcquireAsync(", 1
        )[1].split("private static bool LockingDisabledByRuntime(", 1)[0]

    def test_root_lease_is_held_for_entire_capture_publication(self):
        self.assertIn("using var captureLease = await RecoveryCaptureVolumeGate.AcquireAsync(",
                      self.capture)
        self.assertLess(self.capture.index("using var captureLease ="),
                        self.capture.index("RecoveryCaptureAdmission.EnsureMayCapture("))
        self.assertLess(self.capture.index("using var captureLease ="),
                        self.capture.index("await exportCopy(temporaryCopy);"))
        self.assertLess(self.capture.index("await exportCopy(temporaryCopy);"),
                        self.capture.index("File.Move(temporaryManifest, finalManifest);"))
        self.assertIn("_testVolumeObservation", self.store)
        self.assertIn("internal OfficeRecoveryStore(", self.store)
        self.assertNotIn("TryDelete(temporaryCopy);", self.capture)

    def test_lock_path_is_persistent_and_never_unlinked(self):
        for marker in (
            'LockFileName = ".getdoc-capture.lock"',
            "Directory.CreateDirectory(root);",
            "RequirePlainDirectoryAncestors(root);",
            "RequirePlainLockFileIfPresent(lockPath);",
            "new FileStream(lockPath, FileMode.OpenOrCreate,",
            "FileAccess.ReadWrite, FileShare.ReadWrite,",
            "handle.Lock(0, 1);",
            "handle.Unlock(0, 1);",
            "ConcurrentDictionary<string, SemaphoreSlim>",
            "local.WaitAsync(0, cancellationToken)",
            "local.Release();",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.gate)
        self.assertNotIn("File.Delete(", self.gate)
        self.assertNotIn("Directory.Delete(", self.gate)
        self.assertNotIn("File.Replace(", self.gate)
        self.assertNotIn("FileOptions.DeleteOnClose", self.gate)

    def test_volume_gate_rejects_unknown_network_or_low_free_space(self):
        for marker in (
            "MaximumOfficeCopyBytes = 128L * 1024 * 1024",
            "MinimumFreeBytes = 512L * 1024 * 1024",
            "if (!observed.IsApprovedLocalVolume)",
            "if (observed.AvailableFreeBytes < MinimumFreeBytes)",
            "DriveInfo.GetDrives()",
            "drive.AvailableFreeSpace",
            'kind.Equals("NTFS"',
            '"ext4" or "xfs" or "btrfs" or "overlay" or "tmpfs"',
            "DOTNET_SYSTEM_IO_DISABLEFILELOCKING",
            "System.IO.DisableFileLocking",
            "RequireAvailableLocalSpace(firstVolume);",
            "RequireAvailableLocalSpace(recheckedVolume);",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.gate)
        self.assertEqual(1, self.acquire.count(
            "RequireAvailableLocalSpace(firstVolume);"))
        self.assertEqual(1, self.acquire.count(
            "RequireAvailableLocalSpace(recheckedVolume);"))
        self.assertIn("volumeLocal.WaitAsync(0, cancellationToken)", self.acquire)
        self.assertIn("if (!volumeTransferred)", self.acquire)
        self.assertIn("finally { volumeLocal.Release(); }", self.acquire)

    def test_lease_uses_thread_independent_dispose(self):
        self.assertIn("internal sealed class RecoveryCaptureVolumeLease : IDisposable",
                      self.gate)
        self.assertIn("Interlocked.Exchange(ref _disposed, 1)", self.gate)
        self.assertIn("handle?.Dispose();", self.gate)
        self.assertIn("_local.Release();", self.gate)
        self.assertNotIn("ReleaseMutex(", self.gate)

    def test_deterministic_synthetic_cases_authored(self):
        names = (
            "FirstLeaseBlocksSecondCaptureOnSameRecoveryRoot",
            "ReleasedLeaseAllowsNextCaptureWithoutRemovingLockFile",
            "CanceledAcquisitionNeverCreatesRootOrCallsProbe",
            "LowDiskSpaceFailsBeforeCreatingLockOrRunningExporter",
            "UnsupportedOrUnknownVolumeFailsClosedWithoutCreatingRoot",
            "FreeSpaceIsCheckedAgainWhileHoldingExclusiveLease",
            "RejectedLinkedLockFilePreservesExternalTargetBytes",
            "LockIsReleasedOnCaptureFailureWithoutDeletingScratch",
            "SeparateStoresSharingRootRejectOverlappingExportBeforeItRuns",
            "LowSpaceStoreRefusesExporterAndPreservesSourceWithoutRecoveryFolder",
            "TwoDistinctRecoveryRootsOnSameVolumeCannotOverlapInOneProcess",
            "IndependentSyntheticVolumeKeysDoNotShareLocalGate",
            "VolumeChangedBetweenProbesFailsClosedAndReleasesGate",
            "DistinctRootsReusePersistentSharedCoordinatorFileAfterRelease",
            "LinkedSharedCoordinatorFileRejectsBeforeCreatingRecoveryRoot",
        )
        for name in names:
            with self.subTest(name=name):
                self.assertIn(name, self.tests)
        self.assertEqual(15, self.tests.count("[TestMethod]"))

    def test_gates_remain_not_run_and_release_closed(self):
        step = 'CommandStep("recovery-capture-volume-source-tests"'
        self.assertEqual(1, self.runner.count(step))
        self.assertIn('"-p", "test_recovery_capture_volume_contract.py"',
                      self.runner)
        self.assertLess(self.runner.index(step),
                        self.runner.index('CommandStep("solution-restore"'))
        for name in (
            "gd31j27bm-capture-lock-windows-ntfs-manual-e2e",
            "gd31j27bm-capture-lock-astra-ext4-manual-e2e",
            "gd31j27bm-unsupported-smb-fail-closed-manual-e2e",
            "gd31j27bm-disk-pressure-cancel-crash-manual-e2e",
        ):
            self.assertIn(name, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
