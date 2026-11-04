"""GD-31J.27bo source-only checks for a persistent shared volume gate.

This file is NOT executed in this stage. These are text inspections, not
a proof of cross-process Windows/Astra locks, file identities, or ENOSPC safety.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
COORDINATOR = ROOT / "src/GetDocument.Office/LibreOffice/RecoverySharedVolumeCoordinator.cs"
GATE = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryCaptureVolumeGate.cs"
STORE = ROOT / "src/GetDocument.Office/LibreOffice/OfficeRecoveryStore.cs"
TEST_COORD = ROOT / "tests/GetDocument.Office.Tests/RecoverySharedVolumeCoordinatorTests.cs"
TEST_CAPTURE = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryCaptureVolumeGateTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class SharedVolumeCoordinatorSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.coordinator = COORDINATOR.read_text(encoding="utf-8")
        cls.gate = GATE.read_text(encoding="utf-8")
        cls.store = STORE.read_text(encoding="utf-8")
        cls.coord_tests = TEST_COORD.read_text(encoding="utf-8")
        cls.capture_tests = TEST_CAPTURE.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")
        cls.acquire = cls.gate.split(
            "internal static async Task<RecoveryCaptureVolumeLease> AcquireAsync(", 1
        )[1].split("private static string LocalVolumeGateKey(", 1)[0]

    def test_volume_key_and_filename_are_stable_not_delete_tokens(self):
        for marker in (
            'LockPrefix = ".getdoc-volume-"',
            'LockSuffix = ".lock"',
            "SHA256.HashData(Encoding.UTF8.GetBytes(normalized))",
            "Convert.ToHexString(",
            "string.IsNullOrWhiteSpace(volumeKey) || volumeKey.Length > 2048",
            "DefaultCoordinatorRoot()",
            'Path.Combine(parent, "GET-Document", "recovery-volume-coordinator")',
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.coordinator)

    def test_coordinator_pins_kernel_lock_through_held_handle(self):
        for marker in (
            "RequirePlainDirectoryTree(root);",
            "RequirePlainLockFileIfPresent(lockPath);",
            "new FileStream(lockPath, FileMode.OpenOrCreate,",
            "FileAccess.ReadWrite, FileShare.ReadWrite,",
            "handle.Lock(0, 1);",
            "handle.Unlock(0, 1);",
            "Task.FromResult(lease)",
            "Interlocked.Exchange(ref _handle, null)",
            "Directory.GetUnixFileMode(root)",
            "RecoveryCaptureVolumeGate.ObserveRealVolume(root)",
            "if (!location.IsApprovedLocalVolume)",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.coordinator)
        for forbidden in (
            "File.Delete(", "Directory.Delete(", "File.Move(", "File.Replace(",
            "FileOptions.DeleteOnClose", "TimeSpan.FromDays(", "Process.Kill(",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.coordinator)

    def test_shared_lock_covers_root_mutation_and_document_publication(self):
        self.assertLess(
            self.acquire.index("RecoverySharedVolumeCoordinator.AcquireAsync("),
            self.acquire.index("Directory.CreateDirectory(root);"))
        self.assertLess(
            self.acquire.index("sharedLease = await RecoverySharedVolumeCoordinator.AcquireAsync("),
            self.acquire.index("handle.Lock(0, 1);"))
        self.assertIn("sharedLease = null;", self.acquire)
        self.assertIn("if (!volumeTransferred)", self.acquire)
        self.assertIn("sharedLease?.Dispose();", self.acquire)
        self.assertIn("private readonly RecoverySharedVolumeLease _sharedLease;", self.gate)
        self.assertIn("_sharedLease.Dispose();", self.gate)
        capture = self.store.split(
            "public async Task<OfficeRecoveryCopy> CaptureAsync(", 1
        )[1].split("public OfficeRecoveryIncompleteInventory InspectIncomplete(", 1)[0]
        self.assertIn(
            "using var captureLease = await RecoveryCaptureVolumeGate.AcquireAsync(",
            capture)
        self.assertLess(capture.index("using var captureLease"),
                        capture.index("await exportCopy(temporaryCopy);"))
        self.assertLess(capture.index("await exportCopy(temporaryCopy);"),
                        capture.index("File.Move(temporaryManifest, finalManifest);"))

    def test_exceptions_and_cancellation_release_all_leases(self):
        self.assertIn("catch", self.coordinator)
        self.assertIn("finally { handle.Dispose(); }", self.coordinator)
        self.assertIn("CancellationToken cancellationToken = default", self.coordinator)
        self.assertIn("cancellationToken.ThrowIfCancellationRequested();",
                      self.coordinator)
        self.assertIn("finally { volumeLocal.Release(); }", self.acquire)
        self.assertIn("finally { _volumeLocal.Release(); }", self.gate)
        self.assertNotIn("File.Delete(", self.gate)
        self.assertNotIn("TryDelete(temporaryCopy);", self.store)

    def test_new_synthetic_tests_are_written_but_not_executed(self):
        for name in (
            "SameVolumeKeyAcrossDifferentRecoveryRootsDerivesSameLockName",
            "DifferentVolumeKeysCannotAliasTheSameCoordinatorFile",
            "HeldCoordinatorIsPersistentAcrossCloseAndReopen",
            "PreCancelledAcquireNeverCreatesCoordinatorDirectory",
            "ExistingSymlinkedCoordinatorLockIsRejectedWithoutModifyingTarget",
            "ExistingNonemptyCoordinatorLockIsRejectedAndRetained",
            "EmptyOrInvalidVolumeIdentityCannotCreateCoordinatorPath",
            "SymlinkedCoordinatorDirectoryIsRejectedBeforeLockCreation",
            "NewLinuxCoordinatorDirectoryUsesPrivateOwnerOnlyMode",
            "ExistingWorldReadableLinuxCoordinatorIsDeniedNotChmodded",
            "ExistingGroupWritableLinuxCoordinatorFailsBeforeLockCreation",
        ):
            with self.subTest(name=name):
                self.assertIn(name, self.coord_tests)
        self.assertEqual(11, self.coord_tests.count("[TestMethod]"))
        for name in (
            "DistinctRootsReusePersistentSharedCoordinatorFileAfterRelease",
            "LinkedSharedCoordinatorFileRejectsBeforeCreatingRecoveryRoot",
        ):
            self.assertIn(name, self.capture_tests)
        self.assertEqual(15, self.capture_tests.count("[TestMethod]"))

    def test_manual_platform_gates_are_registered_and_release_is_closed(self):
        step = 'CommandStep("recovery-shared-volume-coordinator-source-tests"'
        self.assertEqual(1, self.runner.count(step))
        self.assertIn(
            '"-p", "test_recovery_shared_volume_coordinator_contract.py"',
            self.runner)
        self.assertLess(self.runner.index(step),
                        self.runner.index('CommandStep("solution-restore"'))
        for gate in (
            "gd31j27bo-windows-two-process-same-volume-manual-e2e",
            "gd31j27bo-astra-two-process-same-volume-manual-e2e",
            "gd31j27bo-crash-restart-stale-lockfile-manual-e2e",
            "gd31j27bo-cross-user-appdata-acl-manual-e2e",
            "gd31j27bo-enospc-admission-race-manual-e2e",
        ):
            self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
