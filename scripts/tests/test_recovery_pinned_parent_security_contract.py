"""GD-31J.27bq: source-only pinned parent and OS permission contracts.

NOT executed here; these checks do not compile .NET, exercise openat2,
attest Windows ACLs, run a GUI, or authorize native writes/deletes.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
LINUX = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryPinnedLinuxParentProbe.cs"
WINDOWS = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryWindowsParentSecurityProbe.cs"
PINNED_WIN = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryWindowsPinnedParentProbe.cs"
STORE = ROOT / "src/GetDocument.Office/LibreOffice/OfficeRecoveryStore.cs"
COORD = ROOT / "src/GetDocument.Office/LibreOffice/RecoverySharedVolumeCoordinator.cs"
GATE = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryCaptureVolumeGate.cs"
ADAPTERS = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryCaptureIdentityAttestation.cs"
LINUX_TEST = ROOT / "tests/GetDocument.Office.Tests/RecoveryPinnedLinuxParentTests.cs"
WIN_TEST = ROOT / "tests/GetDocument.Office.Tests/RecoveryWindowsParentSecurityTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class PinnedParentAndPermissionsContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.linux = LINUX.read_text(encoding="utf-8")
        cls.windows = WINDOWS.read_text(encoding="utf-8")
        cls.pinned_windows = PINNED_WIN.read_text(encoding="utf-8")
        cls.store = STORE.read_text(encoding="utf-8")
        cls.coord = COORD.read_text(encoding="utf-8")
        cls.gate = GATE.read_text(encoding="utf-8")
        cls.adapters = ADAPTERS.read_text(encoding="utf-8")
        cls.linux_test = LINUX_TEST.read_text(encoding="utf-8")
        cls.win_test = WIN_TEST.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")

    def test_linux_requires_actual_preopened_anchor(self):
        for value in (
            "OpenChildRelative(", "SafeFileHandle heldAnchor",
            "heldAnchor.DangerousAddRef(ref pinned)",
            "heldAnchor.DangerousGetHandle().ToInt32()",
            "heldAnchor.DangerousRelease()",
            "RecoveryPinnedLinuxParentLease",
            "Interlocked.Exchange(ref _child, null)",
            "Interlocked.Exchange(ref _anchor, null)",
        ):
            with self.subTest(value=value):
                self.assertIn(value, self.linux)
        self.assertNotIn("Directory.CreateDirectory(", self.linux)
        self.assertNotIn("File.Delete(", self.linux)
        self.assertNotIn("File.Move(", self.linux)
        self.assertNotIn("File.WriteAllText(", self.linux)

    def test_linux_openat2_is_relative_no_follow_and_no_mount_crossing(self):
        for value in (
            "OpenAt2Syscall = 437",
            "OPath = 0x200000",
            "ODirectory = 0x10000",
            "ONoFollow = 0x20000",
            "OCloexec = 0x80000",
            "ResolveNoXdev = 0x01",
            "ResolveNoMagicLinks = 0x02",
            "ResolveNoSymlinks = 0x04",
            "ResolveBeneath = 0x08",
            "Flags = OPath | ODirectory | ONoFollow | OCloexec",
            "Resolve = ResolveBeneath | ResolveNoSymlinks |",
            "ValidateChildName(childName)",
            'DllImport("libc", EntryPoint = "syscall"',
        ):
            with self.subTest(value=value):
                self.assertIn(value, self.linux)
        self.assertIn("if (fd < 0)", self.linux)
        self.assertIn("throw new PlatformNotSupportedException(", self.linux)

    def test_linux_requires_uid_0700_statx_and_immutable_observation(self):
        for value in (
            "StatxUid = 0x0008",
            "StatxMode = 0x0002",
            "StatxMntId = 0x1000",
            "BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(20, 4))",
            "BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(28, 2))",
            "BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(144, 8))",
            "Owner0700 = 0x01C0",
            "GetEffectiveUserId()",
            "RequirePrivateOwner(first, euid)",
            "RequirePrivateOwner(opened, euid)",
            "RequirePrivateOwner(rechecked, euid)",
            "first != rechecked",
            "first.MountId != opened.MountId",
            "first.FileId.DeviceOrVolume != opened.FileId.DeviceOrVolume",
        ):
            with self.subTest(value=value):
                self.assertIn(value, self.linux)

    def test_windows_held_handle_reads_owner_sid_dacl_without_mutation(self):
        for value in (
            "ObserveExistingHandle(", "SafeFileHandle handle",
            "GetSecurityInfo(handle, FileObject,",
            "OwnerSecurityInformation | DaclSecurityInformation",
            "ConvertSidToStringSidW(owner, out buffer)",
            "if (buffer != 0) LocalFree(buffer)",
            "if (securityDescriptor != 0) LocalFree(securityDescriptor)",
            "RecoveryNativeHandleIdProbe.ReadFromExistingHandle(handle)",
            "RecoveryWindowsParentEvidence(sid, dacl != 0, fileId)",
        ):
            with self.subTest(value=value):
                self.assertIn(value, self.windows)
        for bad in ("File.Delete(", "File.Move(", "File.WriteAllText(",
                    "SetSecurityInfo(", "SetFileSecurity("):
            self.assertNotIn(bad, self.windows)

    def test_windows_parent_directory_pin_blocks_final_reparse_only(self):
        for value in (
            "OpenForDiagnostics(", "FileFlagBackupSemantics = 0x02000000",
            "FileFlagOpenReparsePoint = 0x00200000",
            "FileAttributeReparsePoint = 0x400",
            "ReadControl | FileReadAttributes",
            "RecoveryWindowsParentSecurityProbe.ObserveExistingHandle(handle)",
            "RecoveryWindowsPinnedParentLease",
            "Interlocked.Exchange(ref _handle, null)",
        ):
            with self.subTest(value=value):
                self.assertIn(value, self.pinned_windows)
        self.assertIn("Ancestors may still be renamed/reparsed", self.pinned_windows)
        self.assertNotIn("File.Delete(", self.pinned_windows)
        self.assertNotIn("Directory.Delete(", self.pinned_windows)

    def test_windows_gate_never_authorizes_without_ace_review(self):
        for value in (
            "RecoveryWindowsParentDecision.IncompleteFileIdentity",
            "RecoveryWindowsParentDecision.MissingOrInvalidOwner",
            "RecoveryWindowsParentDecision.OwnerMismatch",
            "RecoveryWindowsParentDecision.NullOrUnavailableDacl",
            "RecoveryWindowsParentDecision.RequiresExplicitAceAudit",
            "CanAuthorizeCapture(RecoveryWindowsParentDecision finding) =>",
        ):
            with self.subTest(value=value):
                self.assertIn(value, self.windows)
        self.assertIn("false;", self.windows)
        self.assertNotIn("return RecoveryWindowsParentDecision.Trusted", self.windows)

    def test_no_production_escalation_from_unverified_native_evidence(self):
        for src in (self.store, self.coord, self.gate):
            self.assertNotIn("RecoveryPinnedLinuxParentProbe", src)
            self.assertNotIn("RecoveryWindowsPinnedParentProbe", src)
            self.assertNotIn("RecoveryWindowsParentSecurityGate", src)
        self.assertEqual(2,
            self.adapters.count("SupportsVerifiedRootAndLockIdentity => false;"))
        self.assertNotIn("TryDelete(temporaryCopy);", self.store)

    def test_synthetic_mstest_coverage_authored(self):
        self.assertEqual(7, self.linux_test.count("[TestMethod]"))
        self.assertEqual(9, self.win_test.count("[TestMethod]"))
        for name in (
            "LinuxRelativeOpenPinsAnExistingPrivateChildNoPathReopen",
            "LinuxRelativeOpenRefusesSymlinkChildAndPreservesTarget",
            "PrivateOwnerGuardRejectsWrongUidOrBroaderPermissions",
            "MatchingOwnerAndPresentDaclStillNeedsAceReview",
            "WindowsPinnedDirectoryRejectsFinalReparsePoint",
        ):
            self.assertIn(name, self.linux_test + self.win_test)

    def test_runner_records_manual_not_run_gates(self):
        step = 'CommandStep("recovery-pinned-parent-security-source-tests"'
        self.assertEqual(1, self.runner.count(step))
        self.assertIn(
            '"-p", "test_recovery_pinned_parent_security_contract.py"',
            self.runner)
        self.assertLess(self.runner.index(step),
                        self.runner.index('CommandStep("solution-restore"'))
        for gate in (
            "gd31j27bq-astra-openat2-pinned-parent-manual-e2e",
            "gd31j27bq-linux-uid-mode-mount-alias-manual-e2e",
            "gd31j27bq-windows-held-parent-owner-dacl-manual-e2e",
            "gd31j27bq-windows-ace-privilege-denial-manual-e2e",
            "gd31j27bq-parent-rename-fault-injection-manual-e2e",
        ):
            self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
