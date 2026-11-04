"""GD-31J.27bn: source-only volume and native handle identity contracts.

Text inspections do not execute C#, .NET file locks, native FILE_ID_INFO,
Linux openat2/fstat, NTFS, Astra, SMB, reboots or unlink.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
GATE = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryCaptureVolumeGate.cs"
NATIVE = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryCaptureIdentityAttestation.cs"
STORE = ROOT / "src/GetDocument.Office/LibreOffice/OfficeRecoveryStore.cs"
TEST_GATE = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryCaptureVolumeGateTests.cs"
TEST_NATIVE = ROOT / "tests/GetDocument.Office.Tests/RecoveryCaptureIdentityAttestationTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class RecoveryVolumeIdentitySourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.gate = GATE.read_text(encoding="utf-8")
        cls.native = NATIVE.read_text(encoding="utf-8")
        cls.store = STORE.read_text(encoding="utf-8")
        cls.test_gate = TEST_GATE.read_text(encoding="utf-8")
        cls.test_native = TEST_NATIVE.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")

    def test_per_volume_gate_is_in_process_only_and_released_on_exit(self):
        for marker in (
            "ConcurrentDictionary<string, SemaphoreSlim> VolumeGates",
            "volumeLocal.WaitAsync(0, cancellationToken)",
            "LocalVolumeGateKey(firstVolume, root)",
            "LocalVolumeGateKey(recheckedVolume, root)",
            "if (!volumeTransferred) volumeLocal.Release();",
            "new RecoveryCaptureVolumeLease(handle, local, volumeLocal);",
            "finally { _volumeLocal.Release(); }",
            "VolumeKey: Path.GetFullPath(drive.Name)",
            "VolumeKey = null",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.gate)
        self.assertIn("NOT a native", self.gate)
        self.assertNotIn("File.Delete(", self.gate)
        self.assertNotIn("Directory.Delete(", self.gate)

    def test_lock_path_after_acquisition_is_only_a_diagnostic_check(self):
        self.assertIn("RequirePlainLockFileIfPresent(lockPath);", self.gate)
        self.assertIn("if (!File.Exists(lockPath))", self.gate)
        self.assertLess(self.gate.index("handle.Lock(0, 1);"),
                        self.gate.index("if (!File.Exists(lockPath))"))
        self.assertIn("cannot", self.gate)
        self.assertIn("OS file ID", self.gate)

    def test_native_attestation_is_deliberately_not_production_wired(self):
        for marker in (
            "internal readonly record struct RecoveryCaptureIdentityRequest",
            "internal readonly record struct RecoveryCaptureIdentityEvidence",
            "internal interface IRecoveryCaptureIdentityAdapter",
            "internal static class RecoveryCaptureIdentityGate",
            "evidence.RootAtOpen != evidence.RootAtRecheck",
            "evidence.LockAtOpen != evidence.LockAtRecheck",
            "evidence.LockAtOpen != evidence.LockNameAtRecheck",
            "!evidence.ParentWriterIsolationConfirmed",
            "!evidence.NoFollowEnforced",
            "!evidence.LockHandlePinned",
            "!evidence.LockRangeHeld",
            "RecoveryNativeFileId",
            "SupportsVerifiedRootAndLockIdentity => false;",
            "throw new PlatformNotSupportedException(",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.native)
        self.assertEqual(2, self.native.count(
            "SupportsVerifiedRootAndLockIdentity => false;"))
        self.assertNotIn("RecoveryCaptureIdentityGate", self.store)
        self.assertNotIn("RecoveryCaptureIdentityGate", self.gate)
        for forbidden in (
            "File.Delete(", "File.Move(", "File.Replace(",
            "Directory.Delete(", "unlinkat(", "SetFileInformationByHandle(",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.native)

    def test_root_and_lock_ids_share_platform_volume_and_remain_stable(self):
        for marker in (
            "evidence.RootAtOpen.IsComplete",
            "evidence.LockAtOpen.IsComplete",
            "evidence.LockNameAtRecheck.IsComplete",
            "evidence.RootAtOpen.DeviceOrVolume != evidence.LockAtOpen.DeviceOrVolume",
            "RootOpenedRelativeToTrustedAnchor",
            "NoFollowEnforced",
            "ParentWriterIsolationConfirmed",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.native)

    def test_test_specs_cover_volume_races_and_native_denial(self):
        self.assertEqual(13, self.test_gate.count("[TestMethod]"))
        for required in (
            "TwoDistinctRecoveryRootsOnSameVolumeCannotOverlapInOneProcess",
            "IndependentSyntheticVolumeKeysDoNotShareLocalGate",
            "VolumeChangedBetweenProbesFailsClosedAndReleasesGate",
        ):
            self.assertIn(required, self.test_gate)
        self.assertEqual(7, self.test_native.count("[TestMethod]"))
        for required in (
            "RealAdaptersRefuseToClaimNativeRootAndLockIdentity",
            "ParentReplacedAfterOpenFailsEvenIfFileHandleStayedStable",
            "LockPathReplacedWhileOriginalHandleRemainsOpenFails",
            "MissingNoFollowExclusiveParentOrHeldLockFailsClosed",
            "IdentityPlatformVolumeMismatchAndIncompleteFileIdFail",
            "ConsistentFakeIdentityIsOnlyAnAuditObservationNotDeleteAuthority",
        ):
            self.assertIn(required, self.test_native)

    def test_runner_registers_manual_gates_before_release_and_default_closed(self):
        step = 'CommandStep("recovery-volume-identity-source-tests"'
        self.assertEqual(1, self.runner.count(step))
        self.assertIn('"-p", "test_recovery_volume_identity_contract.py"',
                      self.runner)
        self.assertLess(self.runner.index(step),
                        self.runner.index('CommandStep("solution-restore"'))
        for gate in (
            "gd31j27bn-volume-cross-root-windows-manual-e2e",
            "gd31j27bn-volume-cross-root-astra-manual-e2e",
            "gd31j27bn-native-root-fileid-swap-manual-e2e",
            "gd31j27bn-volume-pressure-multi-process-manual-e2e",
        ):
            self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
