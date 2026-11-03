"""GD-31J.27bd: static-only native recovery identity contract checks.

Source inspection, NOT a .NET build, native API test or OS security proof.
Native Windows/Linux adapters are deliberately unavailable and unregistered.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
CONTRACT = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryNativeIdentityContracts.cs"
ADAPTERS = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryNativeIdentityAdapters.cs"
STORE = ROOT / "src/GetDocument.Office/LibreOffice/OfficeRecoveryStore.cs"
TESTS = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryNativeIdentityContractTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class NativeRecoveryIdentitySourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.contract = CONTRACT.read_text(encoding="utf-8")
        cls.adapters = ADAPTERS.read_text(encoding="utf-8")
        cls.store = STORE.read_text(encoding="utf-8")
        cls.tests = TESTS.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")
        cls.gate = cls.contract.split(
            "internal static class RecoveryNativeIdentityGate", 1
        )[1]

    def test_windows_file_id_uses_full_width_and_linux_inode_device(self):
        for field in ("ulong DeviceOrVolume", "ulong ObjectLow", "ulong ObjectHigh"):
            with self.subTest(field=field):
                self.assertIn(field, self.contract)
        self.assertIn("Platform != RecoveryNativePlatform.Linux || ObjectHigh == 0",
                      self.contract)
        self.assertIn("DeviceOrVolume != 0", self.contract)
        self.assertIn("(ObjectLow != 0 || ObjectHigh != 0)", self.contract)

    def test_relative_key_only_accepts_digest_and_guid_ooxml(self):
        for condition in (
            "sourceFolderHash.Length != 64",
            "sourceFolderHash.All(IsAsciiHex)",
            'Guid.TryParseExact(copyFileName[..32], "N", out _)',
            '".docx" or ".xlsx" or ".pptx"',
            '".part.docx" or ".part.xlsx" or ".part.pptx"',
        ):
            with self.subTest(condition=condition):
                self.assertIn(condition, self.contract)
        self.assertIn("RecoveryRelativeKey.Create(key.SourceFolderHash, key.CopyFileName)",
                      self.gate)
        self.assertNotIn("Path.Combine(", self.gate)

    def test_handle_lease_evidence_requires_parent_and_file_ids(self):
        for field in (
            "RecoveryNativeFileId ParentAtOpen",
            "RecoveryNativeFileId ParentAtRecheck",
            "RecoveryNativeFileId FileAtOpen",
            "RecoveryNativeFileId FileAtRecheck",
            "bool OpenedRelativeToTrustedParent",
            "bool NoFollowEnforced",
            "FileStream Stream",
        ):
            with self.subTest(field=field):
                self.assertIn(field, self.contract)
        self.assertIn("public void Dispose() => Stream.Dispose();", self.contract)

    def test_gate_checks_all_claims_and_disposes_on_refusal(self):
        for marker in (
            "!adapter.SupportsVerifiedDirectoryRelativeOpen",
            "!lease.OpenedRelativeToTrustedParent",
            "!lease.NoFollowEnforced",
            "lease.ParentAtOpen != lease.ParentAtRecheck",
            "lease.FileAtOpen != lease.FileAtRecheck",
            "lease.ParentAtOpen.DeviceOrVolume != lease.FileAtOpen.DeviceOrVolume",
            "lease.Stream.CanWrite",
            "lease.Stream.Length > MaxRecoveryCopyBytes",
            "lease.Dispose();",
            "throw;",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.gate)
        self.assertIn("PlatformNotSupportedException(", self.gate)
        self.assertLess(self.gate.index("lease.Dispose();"),
                        self.gate.index("throw;", self.gate.index("lease.Dispose();")))

    def test_platform_adapters_are_separate_and_explicitly_disabled(self):
        self.assertIn("class WindowsRecoveryNativeIdentityAdapter", self.adapters)
        self.assertIn("class LinuxRecoveryNativeIdentityAdapter", self.adapters)
        self.assertEqual(2, self.adapters.count(
            "public bool SupportsVerifiedDirectoryRelativeOpen => false;"))
        self.assertEqual(2, self.adapters.count(
            "throw new PlatformNotSupportedException("))
        for forbidden in ("DllImport(", "LibraryImport(", "NtCreateFile(",
                          "openat(", "openat2(", "File.OpenHandle(",
                          "File.GetAttributes(", "new FileStream("):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.adapters)

    def test_production_recovery_uses_existing_managed_one_stream_path(self):
        self.assertIn("using var input = OpenRecoveryReadStream(copy);", self.store)
        self.assertIn("VerifyAndHashExportAsync(", self.store)
        self.assertIn("new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true)",
                      self.store)
        for forbidden in ("RecoveryNativeIdentityGate.OpenVerifiedRead(",
                          "new WindowsRecoveryNativeIdentityAdapter(",
                          "new LinuxRecoveryNativeIdentityAdapter("):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.store)

    def test_mstest_cases_cover_paths_and_fail_closed_identities(self):
        names = (
            "RelativeNamesAllowOnlySourceHashAndKnownRecoveryCopyNames",
            "RelativeNamesRejectTraversalPathsStreamsAndUnknownSuffixes",
            "FullWindowsFileIdIncludesBothHighAndLowHalves",
            "LinuxIdentityIncludesDeviceAndInode",
            "DefaultRelativeKeyMustNotReachAnyAdapter",
            "WindowsAndLinuxAdaptersStayUnavailableUntilNativeValidation",
            "VerifiedFakeLeaseMustBeReadOnlyAndRemainsOwnedByCaller",
            "MismatchedParentOrFileIdentityFailsClosedAndClosesStream",
            "UnsupportedEvidenceOrPartialNetworkIdentityFailsClosed",
            "EmptyStreamAndPlatformMismatchAreRejected",
        )
        for name in names:
            with self.subTest(name=name):
                self.assertIn(name, self.tests)
        self.assertEqual(10, self.tests.count("[TestMethod]"))
        self.assertIn("new FakeAdapter(", self.tests)

    def test_runner_requires_manual_platform_evidence_and_blocks_release(self):
        step = "recovery-native-identity-source-tests"
        self.assertEqual(1, self.runner.count('CommandStep("' + step + '"'))
        self.assertIn('"-p", "test_recovery_native_identity_contract.py"',
                      self.runner)
        self.assertLess(self.runner.index('CommandStep("' + step + '"'),
                        self.runner.index('CommandStep("solution-restore"'))
        for manual in (
            "gd31j27bd-native-identity-windows-ntfs-manual-e2e",
            "gd31j27bd-native-identity-astra-ext4-smb-manual-e2e",
        ):
            self.assertIn(manual, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
