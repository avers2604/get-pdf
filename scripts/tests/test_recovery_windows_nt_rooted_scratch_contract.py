"""GD-31J.27cc: Windows NtCreateFile relative-root lab source-only contracts.

Source inspection only: no .NET compilation, Windows native invocation,
NTFS ACL qualification, SMB validation, crash simulation or authorization.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
OFFICE = ROOT / "src/GetDocument.Office/LibreOffice"
SOURCE = OFFICE / "RecoveryWindowsJournalScratchFactory.cs"
TESTS = ROOT / "tests/GetDocument.Office.Tests/RecoveryWindowsJournalScratchFactoryTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"
STORE = OFFICE / "OfficeRecoveryStore.cs"
WINDOWS_PARENT = OFFICE / "RecoveryWindowsParentSecurityProbe.cs"
WINDOWS_ACL = OFFICE / "RecoveryWindowsAclRiskObserver.cs"
ID_PROBE = OFFICE / "RecoveryNativeHandleIdProbe.cs"


class WindowsNtScratchFactorySourceContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = SOURCE.read_text(encoding="utf-8")
        cls.tests = TESTS.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")
        cls.store = STORE.read_text(encoding="utf-8")

    def test_private_single_component_basename_only(self):
        for token in (
            'ScratchSuffix = ".gdjournal.tmp"',
            "basename.Length != 32 + ScratchSuffix.Length",
            "basename.EndsWith(ScratchSuffix, StringComparison.Ordinal)",
            "nonce.All(c =>",
            'Guid.TryParseExact(nonce, "N", out Guid parsed)',
            "parsed == Guid.Empty",
        ):
            self.assertIn(token, self.source)

    def test_native_file_create_is_relative_to_held_root_not_path(self):
        for token in (
            'DllImport("ntdll.dll", EntryPoint = "NtCreateFile"',
            "RootDirectory = heldParent.DangerousGetHandle()",
            "ObjectName = unicodePointer",
            "NtCreateFile(out raw,",
            "FileCreate,",
            "ioStatus.Information != (nuint)FileCreated",
            "Marshal.StringToHGlobalUni(basename)",
            "Marshal.SizeOf<ObjectAttributes>()"
        ):
            self.assertIn(token, self.source)

    def test_create_disposition_only_new_and_exclusive_share(self):
        for token in (
            "FileCreate = 0x00000002",
            "FileCreated = 0x00000002",
            "FileNonDirectoryFile = 0x00000040",
            "FileSynchronousIoNonAlert = 0x00000020",
            "shareAccess, // exclusive by default; CE allows read-only observers",
            "new SafeFileHandle(raw, ownsHandle: true)",
        ):
            self.assertIn(token, self.source)
        self.assertNotIn("FileOverwrite", self.source)
        self.assertNotIn("FileOpenIf", self.source)

    def test_dont_reparse_and_final_tag_checks_are_mandatory(self):
        for token in (
            "ObjDontReparse = 0x00001000",
            "FileOpenReparsePoint = 0x00200000",
            "Attributes = ObjCaseInsensitive | ObjDontReparse",
            "FileSynchronousIoNonAlert | FileNonDirectoryFile |",
            "RequireNormalDirectory(heldParent)",
            "RequireNormalRegularFile(newlyCreated)",
            "RecoveryWindowsScratchTagFinding.ReparseOrWrongType",
            "EvaluateTag(data.Attributes, wantDirectory)",
        ):
            self.assertIn(token, self.source)

    def test_owner_and_acl_observed_on_held_parent_and_child(self):
        for token in (
            "RecoveryWindowsParentSecurityProbe.ObserveExistingHandle(heldParent)",
            "RecoveryWindowsAclRiskObserver.ObserveExistingHandle(heldParent)",
            "RecoveryWindowsParentSecurityProbe.ObserveExistingHandle(newlyCreated)",
            "RecoveryWindowsAclRiskObserver.ObserveExistingHandle(newlyCreated)",
            "SameAclSnapshot(aclBefore, aclAfter)",
            "childSecurity.FileId != childId",
            "RecoveryWindowsParentSecurityGate.Evaluate(parent, expectedOwnerSid)",
            "RecoveryWindowsParentDecision.RequiresExplicitAceAudit",
        ):
            self.assertIn(token, self.source)

    def test_no_effective_rights_claim_from_matching_acl(self):
        for token in (
            "acl.BroadWriteAllowCount != 0",
            "acl.UnreviewedAceCount != 0",
            "acl.DenyAceCount != 0",
            "before.Aces.SequenceEqual(after.Aces)",
            "HasVerifiedEffectiveAccess => false;",
        ):
            self.assertIn(token, self.source)

    def test_parent_and_file_id_use_existing_native_observer(self):
        for token in (
            "RecoveryNativeHandleIdProbe.ReadFromExistingHandle(newlyCreated)",
            "RecoveryWindowsScratchFileFinding.ParentChanged",
            "RecoveryWindowsScratchFileFinding.CrossVolumeOrAlias",
            "newFile.DeviceOrVolume != parentAtOpen.DeviceOrVolume",
            "childNow != CreatedId",
        ):
            self.assertIn(token, self.source)

    def test_lab_flag_prevents_accidental_ntcreatefile(self):
        token = 'Environment.GetEnvironmentVariable("GD31J27CC_NATIVE_LAB")'
        self.assertIn(token, self.source)
        self.assertLess(self.source.index(token), self.source.index("NtCreateFile(out raw,"))
        self.assertIn('throw new InvalidOperationException(', self.source)

    def test_native_memory_and_handles_are_released_on_all_paths(self):
        for token in (
            "heldParent.DangerousAddRef(ref parentPinned)",
            "heldParent.DangerousRelease()",
            "newlyCreated?.Dispose()",
            "stream?.Dispose()",
            "Marshal.FreeHGlobal(unicodePointer)",
            "Marshal.FreeHGlobal(unmanagedName)",
            "parent?.DangerousRelease()",
            "Interlocked.Exchange(ref _parent, null)",
            "Interlocked.Exchange(ref _stream, null)",
        ):
            self.assertIn(token, self.source)

    def test_no_file_name_mutation_or_automatic_cleanup(self):
        for forbidden in (
            "File.Delete(", "File.Move(", "File.Create(", "File.Open(",
            "File.OpenHandle(", "Path.Combine(", "Directory.Delete(",
            "Directory.CreateDirectory(", "SetFileInformationByHandle(",
            "NtSetInformationFile(", "DeleteFileW(", "MoveFileExW("
        ):
            self.assertNotIn(forbidden, self.source)
        for token in (
            "ProductionCreateEnabled => false;",
            "CanCreateByPath => false;",
            "CanAuthorizeCleanup => false;",
            "CanAuthorizePublication => false;",
            "CanAuthorizeRecovery => false;",
            "CanAuthorizeDelete => false;",
            "CanAuthorizePublish => false;",
            "HasTrustedAncestry => false;",
            "HasVerifiedReparseIsolation => false;",
            "HasVerifiedDirectoryDurability => false;",
        ):
            self.assertIn(token, self.source)

    def test_no_production_store_wiring(self):
        self.assertNotIn("RecoveryWindowsJournalScratchFactory.", self.store)
        self.assertNotIn("RecoveryWindowsJournalScratchLease", self.store)
        self.assertIn("CanCreateByPath => false;", self.source)

    def test_eighteen_authored_mstest_cases(self):
        self.assertEqual(18, self.tests.count("[TestMethod]"))
        for name in (
            "PathsDevicesAndSeparatorsAreNotScratchBasenames",
            "DeniesBroadUnknownOrDenyAceAsEffectivePolicyProof",
            "ReparsePointIsRejectedEvenWhenDirectoryBitMatches",
            "DaclChangeDetectionDoesNotProveEffectiveAccess",
            "MissingLabOptInDeniedBeforeWindowsNativeInvocation",
            "ExplicitWindowsLabOnlyCreatesUniqueFixtureWithoutOverwrite"
        ):
            self.assertIn(name, self.tests)

    def test_existing_windows_denial_probes_remain_unmodified(self):
        self.assertIn("CanAuthorizeCapture", WINDOWS_PARENT.read_text(encoding="utf-8"))
        self.assertIn("CanAuthorizeCapture => false;", WINDOWS_ACL.read_text(encoding="utf-8"))
        self.assertIn("RecoveryNativeHandleIdProbe", ID_PROBE.read_text(encoding="utf-8"))

    def test_runner_and_manual_gates_not_run(self):
        step = 'CommandStep("recovery-windows-nt-rooted-scratch-source-tests"'
        self.assertEqual(1, self.runner.count(step))
        self.assertLess(self.runner.index(step),
                        self.runner.index('CommandStep("solution-restore"'))
        self.assertIn(
            '"-p", "test_recovery_windows_nt_rooted_scratch_contract.py"',
            self.runner)
        for gate in (
            "gd31j27cc-windows-nt-relative-create-exclusive-manual-e2e",
            "gd31j27cc-windows-reparse-junction-substitution-manual-e2e",
            "gd31j27cc-windows-ntfs-owner-dacl-token-privileges-manual-e2e",
            "gd31j27cc-windows-dacl-race-after-create-manual-e2e",
            "gd31j27cc-windows-ntstatus-handle-leak-manual-e2e",
            "gd31j27cc-windows-directory-fsync-powerloss-manual-e2e",
            "gd31j27cc-smb-crosshost-create-and-delete-race-manual-e2e",
            "gd31j27cc-worm-original-preservation-manual-e2e",
        ):
            self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
