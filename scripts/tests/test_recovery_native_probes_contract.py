"""GD-31J.27be: source-only contracts for separate OS identity probes.

These checks DO NOT compile or run either native program. The probes MUST NOT
be linked into GetDocument.Office or enable any recovery identity adapter.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
LINUX = ROOT / "probes/recovery-native-identity/linux/openat2_identity_probe.c"
WINDOWS = ROOT / "probes/recovery-native-identity/windows/fileid_identity_probe.c"
ADAPTERS = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryNativeIdentityAdapters.cs"
STORE = ROOT / "src/GetDocument.Office/LibreOffice/OfficeRecoveryStore.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class RecoveryNativeProbesSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.linux = LINUX.read_text(encoding="utf-8")
        cls.windows = WINDOWS.read_text(encoding="utf-8")
        cls.adapters = ADAPTERS.read_text(encoding="utf-8")
        cls.store = STORE.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")

    def test_linux_uses_dirfd_relative_openat2_with_no_symlink_resolution(self):
        for marker in (
            "#include <linux/openat2.h>",
            "SYS_openat2",
            "RESOLVE_BENEATH",
            "RESOLVE_NO_SYMLINKS",
            "RESOLVE_NO_MAGICLINKS",
            "RESOLVE_NO_XDEV",
            "O_NOFOLLOW",
            "O_DIRECTORY",
            "O_CLOEXEC",
            "fstat(",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.linux)
        self.assertNotIn("O_CREAT", self.linux)
        self.assertNotIn("O_TRUNC", self.linux)
        self.assertNotIn("O_WRONLY", self.linux)

    def test_linux_rejects_unknown_kernel_support_without_unsafe_fallback(self):
        for marker in (
            "#ifndef SYS_openat2",
            "ENOSYS",
            "E2BIG",
            "EXIT_UNSUPPORTED",
            "struct open_how",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.linux)
        self.assertNotIn("system(", self.linux)
        self.assertNotIn("execv(", self.linux)
        self.assertNotIn("openat(", self.linux)

    def test_linux_checks_regular_file_size_and_parent_file_identity(self):
        for marker in (
            "S_ISDIR(",
            "S_ISREG(",
            "st_dev",
            "st_ino",
            "MAX_RECOVERY_BYTES",
            "memcmp(",
            "validate_copy_name(",
            "validate_folder_hash(",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.linux)
        self.assertIn("close(copy_fd)", self.linux)
        self.assertIn("close(folder_fd)", self.linux)
        self.assertIn("close(root_fd)", self.linux)

    def test_windows_reads_true_full_fileid_info_from_handles(self):
        for marker in (
            "GetFileInformationByHandleEx(",
            "FileIdInfo",
            "FILE_ID_INFO",
            "VolumeSerialNumber",
            "FileId.Identifier",
            "FileAttributeTagInfo",
            "FILE_ATTRIBUTE_REPARSE_POINT",
            "FILE_STANDARD_INFO",
            "FILE_FLAG_OPEN_REPARSE_POINT",
            "CreateFileW(",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.windows)
        self.assertNotIn("nFileIndexLow", self.windows)
        self.assertNotIn("nFileIndexHigh", self.windows)

    def test_windows_probe_is_explicitly_diagnostic_not_root_relative(self):
        self.assertIn("DIAGNOSTIC ONLY", self.windows)
        self.assertIn("NOT HANDLE-RELATIVE", self.windows)
        self.assertIn("validate_copy_name(", self.windows)
        self.assertIn("validate_folder_hash(", self.windows)
        self.assertIn("CloseHandle(copy_handle)", self.windows)
        self.assertIn("CloseHandle(folder_handle)", self.windows)
        self.assertIn("CloseHandle(root_handle)", self.windows)
        self.assertNotIn("FILE_WRITE_DATA", self.windows)
        self.assertNotIn("CREATE_ALWAYS", self.windows)

    def test_both_probes_never_read_or_modify_document_contents(self):
        for name, source in (("linux", self.linux), ("windows", self.windows)):
            for forbidden in ("File.Delete(", "File.WriteAllBytes(", "unlink(", "rename(",
                              "remove(", "fwrite(", "fread(", "ReadFile(", "WriteFile(",
                              "DeleteFileW(", "MoveFileW("):
                with self.subTest(name=name, forbidden=forbidden):
                    self.assertNotIn(forbidden, source)

    def test_probes_are_not_enabled_in_getdocument(self):
        self.assertEqual(2, self.adapters.count(
            "public bool SupportsVerifiedDirectoryRelativeOpen => false;"))
        self.assertNotIn("RecoveryNativeIdentityGate.OpenVerifiedRead(", self.store)
        self.assertNotIn("DllImport(", self.store)
        self.assertNotIn("SYS_openat2", self.store)

    def test_release_remains_blocked_without_manual_os_and_smb_evidence(self):
        name = "recovery-native-probes-source-tests"
        self.assertEqual(1, self.runner.count('CommandStep("' + name + '"'))
        self.assertIn('"-p", "test_recovery_native_probes_contract.py"',
                      self.runner)
        self.assertLess(self.runner.index('CommandStep("' + name + '"'),
                        self.runner.index('CommandStep("solution-restore"'))
        for gate in (
            "gd31j27be-native-windows-fileid-manual-e2e",
            "gd31j27be-native-astra-openat2-manual-e2e",
            "gd31j27be-native-smb-identity-manual-e2e",
        ):
            self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
