"""GD-31J.27cb: source-only contracts for Linux held-parent scratch creation.

Does not compile .NET, execute openat2/statx, grant security rights, create
files on Windows, or test actual power-loss/ACL/LSM/SMB behaviour.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
OFFICE = ROOT / "src/GetDocument.Office/LibreOffice"
FACTORY = OFFICE / "RecoveryLinuxJournalScratchFactory.cs"
TESTS = ROOT / "tests/GetDocument.Office.Tests/RecoveryLinuxJournalScratchFactoryTests.cs"
STORE = OFFICE / "OfficeRecoveryStore.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class LinuxHeldScratchFactorySourceContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.factory = FACTORY.read_text(encoding="utf-8")
        cls.tests = TESTS.read_text(encoding="utf-8")
        cls.store = STORE.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")

    def test_no_prod_wiring_or_authority_grants(self):
        self.assertNotIn("RecoveryLinuxJournalScratchFactory.", self.store)
        for token in (
            "ProductionCreateEnabled => false;",
            "CanAuthorizePublish => false;",
            "CanAuthorizeDelete => false;",
            "CanCleanupByPath => false;",
            "HasTrustedParentAncestry => false;",
            "HasVerifiedDurableDirectoryEntry => false;",
            "NativeRelativeCreateReady => false;",
            "CanCreateByPath => false;",
            "CanAuthorizeProductionCreate => false;"
        ):
            self.assertIn(token, self.factory)

    def test_no_path_ops_or_delete_or_rename(self):
        for forbidden in (
            "File.Delete(", "File.Move(", "File.Create(", "File.Open(",
            "File.OpenHandle(", "Path.Combine(", "Directory.Delete(",
            "Directory.CreateDirectory(", "unlinkat(", "renameat2(",
            "SetFileInformationByHandle(", "CreateFileW(", "System.IO.File.Delete("
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.factory)

    def test_canonical_lowercase_nonce_and_fixed_suffix(self):
        for token in (
            'ScratchSuffix = ".gdjournal.tmp"',
            "basename.Length != 32 + ScratchSuffix.Length",
            "basename.EndsWith(ScratchSuffix, StringComparison.Ordinal)",
            'Guid.TryParseExact(nonce, "N", out Guid guid)',
            "guid == Guid.Empty",
            "ValidateName(basename);"
        ):
            self.assertIn(token, self.factory)

    def test_linux_only_openat2_flags_are_exclusive(self):
        for token in (
            "OpenAt2Syscall = 437",
            "OReadWrite | OCreate | OExclusive | ONoFollow |",
            "OCloseOnExec",
            "Mode = OwnerReadWrite0600",
            "Resolve = ResolveBeneath | ResolveNoSymlinks |",
            "ResolveNoMagicLinks | ResolveNoXdev",
            "RuntimeInformation.ProcessArchitecture",
            "Architecture.X64 or Architecture.Arm64",
            "SyscallOpenAt2(OpenAt2Syscall,",
            "heldParent.DangerousGetHandle().ToInt32(), basename,",
        ):
            self.assertIn(token, self.factory)

    def test_input_validated_before_creation_and_fail_closed_errno(self):
        self.assertLess(self.factory.index("ValidateName(basename);"),
                        self.factory.index("SyscallOpenAt2(OpenAt2Syscall,"))
        self.assertLess(self.factory.index("RequirePrivateOwner(before, uid);"),
                        self.factory.index("SyscallOpenAt2(OpenAt2Syscall,"))
        self.assertIn("if (error is 38 or 22)", self.factory)
        self.assertIn("throw new IOException(", self.factory)

    def test_statx_private_0600_regular_file_and_mount_id(self):
        self.assertIn("RecoveryLinuxNamedPairProbe.ParseRegularStatx(stat,", self.factory)
        for token in (
            "StatxType | StatxMode | StatxNlink | StatxUid |",
            "StatxIno | StatxMntId",
            "AtEmptyPath | AtSymlinkNoFollow",
            "created.LinkCount != 1",
            "created.OwnerUid != effectiveUid",
            "created.MountId != before.MountId",
            "created.Id.DeviceOrVolume != before.FileId.DeviceOrVolume",
            "created.Id == before.FileId",
        ):
            self.assertIn(token, self.factory)

    def test_held_parent_pinned_across_native_create_and_checks(self):
        for token in (
            "heldParent.DangerousAddRef(ref parentPinned)",
            "heldParent.DangerousRelease()",
            "RecoveryPinnedLinuxParentProbe.ReadExistingDirectoryEvidence(",
            "GetEffectiveUserId() != uid",
            "RecoveryLinuxJournalScratchFinding.ObservedButNotAuthorized",
        ):
            self.assertIn(token, self.factory)
        self.assertLess(
            self.factory.index("SyscallOpenAt2(OpenAt2Syscall,"),
            self.factory.index("RecoveryLinuxParentEvidence after ="))

    def test_new_descriptor_owned_and_never_unlinked_even_on_error(self):
        for token in (
            "new SafeFileHandle((IntPtr)fd, ownsHandle: true)",
            "stream = new FileStream(newFile, FileAccess.ReadWrite,",
            "newFile = null;",
            "stream?.Dispose();",
            "newFile?.Dispose();",
            "Interlocked.Exchange(ref _stream, null)",
            "Interlocked.Exchange(ref _parent, null)",
            "parent?.DangerousRelease();",
        ):
            self.assertIn(token, self.factory)
        self.assertNotIn("File.Delete", self.factory)

    def test_cancelled_operation_leaves_artifact_for_review(self):
        self.assertIn("cancellationToken.ThrowIfCancellationRequested();", self.factory)
        self.assertIn("On any failure after O_CREAT", self.factory)
        self.assertIn("NEVER unlink it.", self.factory)

    def test_native_create_needs_explicit_lab_opt_in(self):
        self.assertIn('Environment.GetEnvironmentVariable("GD31J27CB_NATIVE_LAB")',
                      self.factory)
        self.assertIn('throw new InvalidOperationException(', self.factory)
        self.assertLess(self.factory.index('Environment.GetEnvironmentVariable("GD31J27CB_NATIVE_LAB")'),
                        self.factory.index("SyscallOpenAt2(OpenAt2Syscall,"))

    def test_windows_pathname_fallback_unavailable(self):
        self.assertIn("internal static class RecoveryWindowsJournalScratchGate", self.factory)
        for token in ("NativeRelativeCreateReady => false;",
                      "CanCreateByPath => false;",
                      "CanAuthorizeProductionCreate => false;"):
            self.assertIn(token, self.factory)

    def test_synthetic_tests_and_opt_in_platform_fixture(self):
        self.assertEqual(16, self.tests.count("[TestMethod]"))
        for token in (
            "CanonicalNonceAndSuffixAccepted",
            "TraversalSlashBackslashAndEmbeddedNullRejected",
            "CrossMountAndDeviceChangeRejected",
            "HardLinkAndAliasedFileIdRejected",
            "WindowsCannotUsePathFallback",
            "NativeCreateWithoutExplicitLabOptInIsBlockedBeforeSyscall",
            "GD31J27CB_NATIVE_LAB",
            "File.SetUnixFileMode(dir, UnixFileMode.UserRead |",
            "ExplicitOptInLinuxScratchCreateIsExclusiveAndNeverDeletesOnDispose",
            "Directory.Delete(dir, recursive: true);"
        ):
            self.assertIn(token, self.tests)

    def test_runner_manual_gates_and_release_denial(self):
        step = 'CommandStep("recovery-linux-scratch-factory-source-tests"'
        self.assertEqual(1, self.runner.count(step))
        self.assertLess(self.runner.index(step),
                        self.runner.index('CommandStep("solution-restore"'))
        self.assertIn(
            '"-p", "test_recovery_linux_scratch_factory_contract.py"',
            self.runner)
        for gate in (
            "gd31j27cb-astra-openat2-excl-no-follow-manual-e2e",
            "gd31j27cb-astra-existing-name-no-overwrite-manual-e2e",
            "gd31j27cb-astra-uid-mode-nlink-mount-manual-e2e",
            "gd31j27cb-astra-cancel-after-create-orphan-manual-e2e",
            "gd31j27cb-windows-relative-handle-create-pending-manual-e2e",
            "gd31j27cb-smb-crosshost-create-excl-manual-e2e",
            "gd31j27cb-astra-namespace-acl-lsm-ancestry-manual-e2e",
            "gd31j27cb-worm-and-path-delete-denial-manual-e2e",
        ):
            self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
