"""GD-31J.27bw: offline source-only tests for Linux held-name observation.

These tests are NOT Astra Linux runtime evidence and grant NO write/delete rights.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
OFFICE = ROOT / "src/GetDocument.Office/LibreOffice"
PROBE = OFFICE / "RecoveryLinuxNamedPairProbe.cs"
TESTS = ROOT / "tests/GetDocument.Office.Tests/RecoveryLinuxNamedPairProbeTests.cs"
STORE = OFFICE / "OfficeRecoveryStore.cs"
PAIR_GATE = OFFICE / "RecoveryPairMutationGate.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class LinuxNamedPairProbeContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.probe = PROBE.read_text(encoding="utf-8")
        cls.tests = TESTS.read_text(encoding="utf-8")
        cls.store = STORE.read_text(encoding="utf-8")
        cls.gate = PAIR_GATE.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")

    def test_strict_pair_names_without_path_or_temp_suffix(self):
        for token in ("ValidatePairNames(", "Guid.TryParseExact(nonce, \"N\"",
                      'manifestName != nonce + ".json"',
                      'extension is not (".docx" or ".xlsx" or ".pptx")',
                      "copyName[..nonceLength]"):
            with self.subTest(token=token):
                self.assertIn(token, self.probe)

    def test_no_follow_parent_relative_file_names(self):
        for token in ("OpenAt2Syscall = 437", "OPath = 0x200000",
                      "ONoFollow = 0x20000", "OCloexec = 0x80000",
                      "ResolveBeneath = 0x08", "ResolveNoSymlinks = 0x04",
                      "ResolveNoXdev = 0x01", "ResolveNoMagicLinks = 0x02",
                      "parent.DangerousGetHandle().ToInt32()",
                      "SyscallOpenAt2(", "OpenRelativeReadOnly("):
            self.assertIn(token, self.probe)

    def test_statx_explicitly_rejects_symlink_and_hardlink(self):
        for token in ("StatxNlink = 0x0004", "StatxMntId = 0x1000",
                      "StatxBytes = 256", "RegularFileType = 0x8000",
                      "Private0600 = 0x0180", "(mode & 0xF000) != RegularFileType",
                      "links != 1", "owner != expectedEffectiveUid",
                      "RecoveryNativeHandleIdProbe.ParseLinuxStatx(data)",
                      "AtEmptyPath | AtSymlinkNoFollow"):
            self.assertIn(token, self.probe)

    def test_reobserves_named_and_held_handles(self):
        for token in ("using SafeFileHandle namedCopy",
                      "using SafeFileHandle namedManifest",
                      "using SafeFileHandle laterCopy",
                      "using SafeFileHandle laterManifest",
                      "CopyNamedAtRecheck", "ManifestNamedAtRecheck",
                      "CopyHeldAtRecheck", "ManifestHeldAtRecheck",
                      "RecoveryLinuxNamedPairFinding.NameDoesNotMatchHeldHandle",
                      "RecoveryLinuxNamedPairFinding.CrossMountOrDevice",
                      "RecoveryLinuxNamedPairFinding.ParentChanged"):
            self.assertIn(token, self.probe)

    def test_pins_parent_copy_manifest_during_native_observation(self):
        for token in ("heldParent.DangerousAddRef(ref parentPinned)",
                      "heldCopy.DangerousAddRef(ref copyPinned)",
                      "heldManifest.DangerousAddRef(ref manifestPinned)",
                      "if (manifestPinned) heldManifest.DangerousRelease()",
                      "if (copyPinned) heldCopy.DangerousRelease()",
                      "if (parentPinned) heldParent.DangerousRelease()",
                      "RecoveryPinnedLinuxParentProbe.RequirePrivateOwner(",
                      "GetEffectiveUserId() != uid"):
            self.assertIn(token, self.probe)

    def test_denial_only_with_no_mutations_and_no_production_wiring(self):
        self.assertIn("CanAuthorizePublish => false;", self.probe)
        self.assertIn("CanAuthorizeDelete => false;", self.probe)
        self.assertIn("CanAuthorizeMutation => false;", self.probe)
        for forbidden in ("File.Delete(", "File.Move(", "unlinkat(",
                          "renameat2(", "File.Open(", "Directory.Delete(",
                          "SetFileInformationByHandle(", "FileStream("):
            self.assertNotIn(forbidden, self.probe)
        self.assertNotIn("RecoveryLinuxNamedPairProbe.", self.store)
        self.assertIn("CanDeleteByPath => false", self.gate)
        self.assertIn("CanPublishByPath => false", self.gate)

    def test_twelve_synthetic_mstest_regressions_authored(self):
        self.assertEqual(12, self.tests.count("[TestMethod]"))
        for token in ("NameValidationRejectsTraversalTempAndMixedGuids",
                      "SymlinkDirectoryAndDevicesAreNotRegularFiles",
                      "BroadPermissionsForeignUidOrHardlinksAreRejected",
                      "RenamedCopyOrManifestNameIsRejected",
                      "StableHeldAndNamedIdsGiveObservationOnly",
                      "CancelledProbeRefusesWithoutFilesOrMutation"):
            self.assertIn(token, self.tests)

    def test_manual_platform_gates_and_release_denial(self):
        step = 'CommandStep("recovery-linux-named-pair-source-tests"'
        self.assertEqual(1, self.runner.count(step))
        self.assertLess(self.runner.index(step),
                        self.runner.index('CommandStep("solution-restore"'))
        self.assertIn(
            '"-p", "test_recovery_linux_named_pair_contract.py"',
            self.runner)
        for gate in (
            "gd31j27bw-astra-openat2-name-symlink-hardlink-manual-e2e",
            "gd31j27bw-astra-rename-during-held-fd-manual-e2e",
            "gd31j27bw-windows-no-native-delete-publish-manual-e2e",
            "gd31j27bw-smb-cross-host-race-manual-e2e",
        ):
            self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
