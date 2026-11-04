"""GD-31J.27bp: source-only contracts for Windows/Linux native HANDLE
identity, no-follow lock-name diagnostics, and private Linux coordinator ACL.

This verifies source text ONLY. It does not compile, run tests, call native
APIs, attest filesystems, or authorize production recovery/delete actions.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
HANDLE = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryNativeHandleIdProbe.cs"
NAME = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryNativeLockNameProbe.cs"
COORD = ROOT / "src/GetDocument.Office/LibreOffice/RecoverySharedVolumeCoordinator.cs"
STORE = ROOT / "src/GetDocument.Office/LibreOffice/OfficeRecoveryStore.cs"
GATE = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryCaptureVolumeGate.cs"
ATTEST = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryCaptureIdentityAttestation.cs"
HANDLE_TESTS = ROOT / "tests/GetDocument.Office.Tests/RecoveryNativeHandleIdProbeTests.cs"
NAME_TESTS = ROOT / "tests/GetDocument.Office.Tests/RecoveryNativeLockNameProbeTests.cs"
COORD_TESTS = ROOT / "tests/GetDocument.Office.Tests/RecoverySharedVolumeCoordinatorTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class RecoveryNativeHandleProbeSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.handle = HANDLE.read_text(encoding="utf-8")
        cls.name = NAME.read_text(encoding="utf-8")
        cls.coord = COORD.read_text(encoding="utf-8")
        cls.store = STORE.read_text(encoding="utf-8")
        cls.gate = GATE.read_text(encoding="utf-8")
        cls.attest = ATTEST.read_text(encoding="utf-8")
        cls.handle_tests = HANDLE_TESTS.read_text(encoding="utf-8")
        cls.name_tests = NAME_TESTS.read_text(encoding="utf-8")
        cls.coord_tests = COORD_TESTS.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")

    def test_native_id_reads_only_already_opened_handles(self):
        for marker in (
            "ReadFromExistingHandle(SafeFileHandle handle)",
            "if (handle.IsClosed || handle.IsInvalid)",
            "ReadWindowsFileIdInfo(handle)",
            "ReadLinuxStatxHandle(handle)",
            "handle.DangerousAddRef(ref added)",
            "handle.DangerousGetHandle().ToInt32()",
            "handle.DangerousRelease()",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.handle)
        for forbidden in (
            "File.ReadAllText(", "File.ReadAllBytes(", "File.Delete(",
            "File.Move(", "File.Copy(", "File.Open(", "new FileStream(",
            "Directory.CreateDirectory(", "Directory.Delete(",
        ):
            self.assertNotIn(forbidden, self.handle)

    def test_windows_reads_full_128_bit_file_id_info(self):
        for marker in (
            "FileIdInfoClass = 0x12",
            "Marshal.SizeOf<WindowsFileIdInfo>()",
            "GetFileInformationByHandleEx(handle, FileIdInfoClass",
            "info.VolumeSerialNumber",
            "info.FileIdLow",
            "info.FileIdHigh",
            'DllImport("kernel32.dll"',
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.handle)
        self.assertNotIn("GetFileInformationByHandle(", self.handle)

    def test_linux_uses_statx_empty_path_and_checks_layout(self):
        for marker in (
            "AtEmptyPath = 0x1000",
            "AtSymlinkNoFollow = 0x0100",
            "StatxMntId = 0x1000",
            "StatxBufferBytes = 256",
            'Statx(fd, "", AtEmptyPath | AtSymlinkNoFollow,',
            "BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(0, 4))",
            "BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(28, 2))",
            "BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(32, 8))",
            "BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(136, 4))",
            "BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(140, 4))",
            "BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(144, 8))",
            "kind is not (RegularFile or DirectoryFile)",
            "if (mountId == 0)",
            'DllImport("libc", EntryPoint = "statx"',
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.handle)
        self.assertIn("Architecture.X64 or Architecture.Arm64", self.handle)
        self.assertIn("if (!result.IsComplete)", self.handle)

    def test_name_comparison_uses_os_no_follow_last_component(self):
        for marker in (
            "VerifyCurrentName(",
            "RecoveryNativeHandleIdProbe.ReadFromExistingHandle(heldLockHandle)",
            "OpenNoFollowForMetadata(Path.GetFullPath(lockPath))",
            "RecoveryNativeHandleIdProbe.ReadFromExistingHandle(fresh)",
            "if (held != named)",
            "FileFlagOpenReparsePoint = 0x00200000",
            "FileAttributeTagInfoClass = 9",
            "ReparsePointAttribute = 0x00000400",
            "LinuxOPath = 0x200000",
            "LinuxONoFollow = 0x20000",
            "LinuxOCloexec = 0x80000",
            "new SafeFileHandle((IntPtr)fd, ownsHandle: true)",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.name)
        for forbidden in (
            "File.Delete(", "File.Move(", "Directory.Delete(",
            "File.Replace(", "File.WriteAll", "SetFileInformationByHandle(",
            "chmod(", "unlinkat(",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.name)
        self.assertIn("does not defend against a mutable ancestor", self.name)

    def test_linux_coordinator_creates_0700_and_never_chmods_old_root(self):
        for marker in (
            "Directory.CreateDirectory(root, OwnerOnly);",
            "UnixFileMode.UserRead |",
            "UnixFileMode.UserWrite | UnixFileMode.UserExecute",
            "if ((mode & OwnerOnly) != OwnerOnly ||",
            "(mode & ~OwnerOnly) != (UnixFileMode)0",
            "Directory.GetUnixFileMode(root)",
            "RequirePlainDirectoryTree(root);",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.coord)
        self.assertNotIn("Directory.SetUnixFileMode(root", self.coord)
        self.assertNotIn("File.SetUnixFileMode(root", self.coord)
        self.assertNotIn("Directory.Delete(", self.coord)
        self.assertNotIn("File.Delete(", self.coord)

    def test_new_native_probes_are_not_production_security_authority(self):
        for file in (self.store, self.gate, self.coord):
            self.assertNotIn("RecoveryNativeHandleIdProbe.", file)
            self.assertNotIn("RecoveryNativeLockNameProbe.", file)
        self.assertEqual(2, self.attest.count(
            "SupportsVerifiedRootAndLockIdentity => false;"))
        self.assertIn("Not wired into production capture", self.handle)
        self.assertIn("Not registered with production capture yet", self.name)

    def test_synthetic_and_os_gated_test_specs_are_present(self):
        self.assertEqual(9, self.handle_tests.count("[TestMethod]"))
        self.assertEqual(5, self.name_tests.count("[TestMethod]"))
        self.assertEqual(11, self.coord_tests.count("[TestMethod]"))
        for name in (
            "RepeatedNativeReadOfOneLiveHandleIsStableWithoutPathReopen",
            "DifferentFilesOnOneVolumeHaveDifferentNativeIdentifiers",
            "LinuxStatxParserRejectsMissingInodeOrMountBits",
            "RenamedCurrentLockNameCannotImpersonateHeldHandle",
            "SymlinkedNameIsRejectedWithoutReadingOrModifyingTarget",
            "ExistingWorldReadableLinuxCoordinatorIsDeniedNotChmodded",
            "NewLinuxCoordinatorDirectoryUsesPrivateOwnerOnlyMode",
        ):
            with self.subTest(name=name):
                self.assertIn(name, self.handle_tests +
                              self.name_tests + self.coord_tests)

    def test_runner_and_manual_platform_gates_are_unexecuted(self):
        step = 'CommandStep("recovery-native-handle-probe-source-tests"'
        self.assertEqual(1, self.runner.count(step))
        self.assertIn(
            '"-p", "test_recovery_native_handle_probe_contract.py"',
            self.runner)
        self.assertLess(self.runner.index(step),
                        self.runner.index('CommandStep("solution-restore"'))
        for gate in (
            "gd31j27bp-windows-full-fileid-ntfs-manual-e2e",
            "gd31j27bp-astra-statx-ext4-manual-e2e",
            "gd31j27bp-nofollow-lock-rename-race-manual-e2e",
            "gd31j27bp-linux-coordinator-private-acl-manual-e2e",
            "gd31j27bp-windows-dacl-owner-sid-manual-e2e",
        ):
            with self.subTest(gate=gate):
                self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
