"""GD-31J.27cd source-only guardrails for dual name lookups and manifest claims.

No actual NTFS/Astra/SMB I/O, .NET compilation, native platform tests,
manifest reading or file deletion is executed by these source checks.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
OFFICE = ROOT / "src/GetDocument.Office/LibreOffice"
TESTS = ROOT / "tests/GetDocument.Office.Tests"
RUNNER = ROOT / "scripts/office_acceptance.py"

class RecoveryScratchNameAndPairSourceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.linux = (OFFICE / "RecoveryLinuxScratchNameIdentityProbe.cs").read_text(encoding="utf-8")
        cls.windows = (OFFICE / "RecoveryWindowsScratchNameIdentityProbe.cs").read_text(encoding="utf-8")
        cls.pair = (OFFICE / "RecoveryScratchPairClaimReview.cs").read_text(encoding="utf-8")
        cls.store = (OFFICE / "OfficeRecoveryStore.cs").read_text(encoding="utf-8")
        cls.cc = (OFFICE / "RecoveryWindowsJournalScratchFactory.cs").read_text(encoding="utf-8")
        cls.linux_tests = (TESTS / "RecoveryLinuxScratchNameIdentityTests.cs").read_text(encoding="utf-8")
        cls.windows_tests = (TESTS / "RecoveryWindowsScratchNameIdentityTests.cs").read_text(encoding="utf-8")
        cls.pair_tests = (TESTS / "RecoveryScratchPairClaimReviewTests.cs").read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")

    def test_linux_held_parent_and_file_pinned(self):
        for term in (
            "heldParent.DangerousAddRef(ref parentPinned)",
            "heldFile.DangerousAddRef(ref filePinned)",
            "heldParent.DangerousRelease()",
            "heldFile.DangerousRelease()",
            "RecoveryPinnedLinuxParentProbe.ReadExistingDirectoryEvidence",
            "RecoveryPinnedLinuxParentProbe.RequirePrivateOwner",
        ):
            self.assertIn(term, self.linux)

    def test_linux_two_separate_no_follow_openat2_name_lookups(self):
        for term in (
            "using SafeFileHandle firstName = OpenRelativeReadOnly",
            "using SafeFileHandle secondName = OpenRelativeReadOnly",
            "OPath | ONoFollow | OCloexec",
            "ResolveBeneath | ResolveNoSymlinks",
            "ResolveNoMagicLinks | ResolveNoXdev",
            "SyscallOpenAt2(OpenAt2Syscall",
            "RecoveryLinuxJournalScratchFactory.ValidateName(basename)",
        ):
            self.assertIn(term, self.linux)

    def test_linux_rechecks_inode_and_mount(self):
        for term in (
            "RecoveryLinuxJournalScratchFactory.ReadPrivateHeldRegular(",
            "if (parent != evidence.ParentAfter)",
            "x.MountId != parent.MountId",
            "x.Id.DeviceOrVolume != parent.FileId.DeviceOrVolume",
            "x.OwnerUid != parent.OwnerUid",
            "files.Any(x => x != evidence.HeldBefore)",
            "NameDoesNotMatchHeld",
        ):
            self.assertIn(term, self.linux)

    def test_windows_name_open_readonly_relative(self):
        for term in (
            'DllImport("ntdll.dll", EntryPoint = "NtOpenFile"',
            "RootDirectory = parent.DangerousGetHandle()",
            "FileReadAttributes | ReadControl | Synchronize",
            "FileNonDirectoryFile | FileSynchronousIoNonAlert |",
            "FileOpenReparsePoint",
            "ObjCaseInsensitive | ObjDontReparse",
            "using SafeFileHandle first = OpenNameReadOnly(",
            "using SafeFileHandle second = OpenNameReadOnly(",
        ):
            self.assertIn(term, self.windows)

    def test_windows_share_zero_disclosed_and_failure_denied(self):
        self.assertIn("CreatorSharePolicyAllowsReopen => false;", self.windows)
        self.assertIn("StatusSharingViolation = unchecked((int)0xC0000043)", self.windows)
        self.assertIn("ExclusiveShareDenied", self.windows)
        self.assertIn("STATUS_SHARING_VIOLATION", self.windows)
        self.assertIn("0, // deny concurrent opens while this lab handle is held", self.cc)
        self.assertNotIn("CreatorSharePolicyAllowsReopen => true", self.windows)

    def test_windows_owner_acl_reparse_and_fileids(self):
        for term in (
            "RecoveryWindowsParentSecurityProbe.ObserveExistingHandle",
            "RecoveryWindowsAclRiskObserver.ObserveExistingHandle",
            "RecoveryNativeHandleIdProbe.ReadFromExistingHandle",
            "RecoveryWindowsJournalScratchFactory.SameAclSnapshot(",
            "RecoveryWindowsJournalScratchFactory.EvaluateTag(",
            "RecoveryWindowsScratchTagFinding.NormalTypeButUntrusted",
            "RecoveryWindowsScratchNameFinding.NameDoesNotMatchHeld",
            "heldParent.DangerousAddRef(ref parentPinned)",
            "heldFile.DangerousAddRef(ref filePinned)",
        ):
            self.assertIn(term, self.windows)

    def test_windows_explicit_lab_gate_before_ntopen(self):
        term = 'Environment.GetEnvironmentVariable("GD31J27CD_NATIVE_LAB")'
        self.assertIn(term, self.windows)
        self.assertLess(self.windows.index(term), self.windows.index("NtOpenFile(out raw,"))
        self.assertIn("throw new InvalidOperationException(", self.windows)

    def test_pair_rejects_conflicts_and_missing_artefacts(self):
        for term in (
            "RecoveryScratchPairFinding.InvalidEvidence",
            "RecoveryScratchPairFinding.UntrustedArtifacts",
            "RecoveryScratchPairFinding.MissingComponent",
            "RecoveryScratchPairFinding.JournalIncomplete",
            "RecoveryScratchPairFinding.NameBindingUnverified",
            "RecoveryScratchPairFinding.ManifestConflict",
            "RecoveryScratchPairFinding.ClaimedConsistentUntrusted",
        ):
            self.assertIn(term, self.pair)
        self.assertIn("link.ExpectedTransactionId != link.ManifestTransactionId", self.pair)
        self.assertIn("link.ObservedCopySha256, link.ManifestCopySha256", self.pair)

    def test_pair_has_no_native_reader_or_permission(self):
        for term in (
            "NativeManifestReaderEnabled => false;",
            "CanAuthorizeMutation => false;",
            "CanRestore => false;",
            "CanDelete => false;",
            "CanPublish => false;",
            "HasAuthenticManifest => false;",
            "HasVerifiedOoxmlContent => false;",
        ):
            self.assertIn(term, self.pair)

    def test_new_modules_do_not_create_mutate_or_delete(self):
        for file in (self.linux, self.windows, self.pair):
            for forbidden in (
                "File.Delete(", "File.Move(", "File.Create(", "File.Open(",
                "Path.Combine(", "Directory.Delete(", "Directory.CreateDirectory(",
                "NtCreateFile(", "unlinkat(", "renameat2(", "SetLength(",
                "File.Write", "NtSetInformationFile(", "MoveFileExW(",
            ):
                with self.subTest(forbidden=forbidden):
                    self.assertNotIn(forbidden, file)
        self.assertNotIn("RecoveryLinuxScratchNameIdentityProbe", self.store)
        self.assertNotIn("RecoveryWindowsScratchNameIdentityProbe", self.store)
        self.assertNotIn("RecoveryScratchPairClaimReview", self.store)

    def test_csharp_regression_methods_authored(self):
        self.assertEqual(12, self.linux_tests.count("[TestMethod]"))
        self.assertEqual(13, self.windows_tests.count("[TestMethod]"))
        self.assertEqual(12, self.pair_tests.count("[TestMethod]"))
        self.assertIn("NameReplacedBetweenTwoOpenat2CallsIsDetected", self.linux_tests)
        self.assertIn("NtSharingViolationIsExplicitUnavailableNotSuccess", self.windows_tests)
        self.assertIn("ManifestJournalHeadMismatchIsRejected", self.pair_tests)

    def test_all_manual_gates_remain_unresolved(self):
        for gate in (
            "gd31j27cd-linux-name-replacement-between-opens-manual-e2e",
            "gd31j27cd-windows-nt-share-zero-reopen-manual-e2e",
            "gd31j27cd-windows-reparse-substitution-during-open-manual-e2e",
            "gd31j27cd-linux-held-parent-during-rename-manual-e2e",
            "gd31j27cd-smb-cross-host-name-replace-manual-e2e",
            "gd31j27cd-manifest-journal-digest-mismatch-manual-e2e",
            "gd31j27cd-restart-file-id-name-binding-manual-e2e",
            "gd31j27cd-worm-original-no-delete-manual-e2e",
        ):
            self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)

    def test_runner_registers_source_checks_before_restore(self):
        step = 'CommandStep("recovery-scratch-name-pair-source-tests"'
        self.assertEqual(1, self.runner.count(step))
        self.assertLess(self.runner.index(step),
                        self.runner.index('CommandStep("solution-restore"'))
        self.assertIn('"-p", "test_recovery_scratch_name_pair_contract.py"', self.runner)


if __name__ == "__main__":
    unittest.main()
