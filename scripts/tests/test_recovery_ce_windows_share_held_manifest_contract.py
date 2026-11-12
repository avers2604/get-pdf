"""GD-31J.27ce: source-only safety contracts for Windows read-share,
parent FILE_ADD_FILE rights, and bounded held journal/manifest double read.

These checks do not run Windows NtCreateFile, open a production directory,
test SMB, compile C#, or independently authenticate recovery contents.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
OFFICE = ROOT / "src/GetDocument.Office/LibreOffice"
TESTS = ROOT / "tests/GetDocument.Office.Tests"
RUNNER = ROOT / "scripts/office_acceptance.py"


class WindowsReadShareContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = (OFFICE / "RecoveryWindowsJournalScratchFactory.cs").read_text(encoding="utf-8")
        cls.tests = (TESTS / "RecoveryWindowsScratchSharePolicyTests.cs").read_text(encoding="utf-8")

    def test_default_remains_exclusive(self):
        self.assertIn("RecoveryWindowsScratchShareMode.Exclusive => 0", self.source)
        self.assertIn("RecoveryWindowsScratchShareMode.Exclusive;", self.source)
        self.assertIn("shareAccess, // exclusive by default; CE allows read-only observers", self.source)

    def test_read_sharing_does_not_enable_write_or_delete(self):
        self.assertIn("RecoveryWindowsScratchShareMode.ReadOnlyObservers => FileShareRead", self.source)
        self.assertIn("FileShareRead = 0x00000001", self.source)
        self.assertNotIn("FileShareWrite = ", self.source)
        self.assertNotIn("FileShareDelete = ", self.source)

    def test_two_independent_lab_flags_required_before_ntcreate(self):
        creator = self.source.index("NtCreateFile(out raw,")
        self.assertLess(self.source.index('Environment.GetEnvironmentVariable("GD31J27CC_NATIVE_LAB")'), creator)
        self.assertLess(self.source.index('Environment.GetEnvironmentVariable("GD31J27CE_READ_SHARE_LAB")'), creator)
        self.assertIn("throw new InvalidOperationException(", self.source)

    def test_read_sharing_has_no_production_authority(self):
        for term in (
            "ProductionReadSharingEnabled => false;",
            "HasVerifiedNtfsSharingSemantics => false;",
            "HasVerifiedSmbSharingSemantics => false;",
            "CanAuthorizePublication => false;",
            "CanAuthorizeCleanup => false;"
        ):
            self.assertIn(term, self.source)

    def test_seven_mstest_sharing_cases_authored(self):
        self.assertEqual(7, self.tests.count("[TestMethod]"))
        self.assertIn("ExplicitReadObserversShareOnlyReadAndNotDeleteOrWrite", self.tests)


class WindowsParentRightContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = (OFFICE / "RecoveryWindowsScratchParentLabOpener.cs").read_text(encoding="utf-8")
        cls.tests = (TESTS / "RecoveryWindowsScratchParentLabOpenerTests.cs").read_text(encoding="utf-8")

    def test_explicit_directory_rights_requested(self):
        for term in (
            "FileAddFile = 0x00000002",
            "FileTraverse = 0x00000020",
            "FileReadAttributes = 0x00000080",
            "ReadControl = 0x00020000",
            "Synchronize = 0x00100000",
            "FileAddFile | FileTraverse | FileReadAttributes | ReadControl | Synchronize",
            "CreateFileW(canonical, RequiredAccessMask,"
        ):
            self.assertIn(term, self.source)

    def test_parent_opener_is_operator_scoped_only(self):
        for term in (
            '"GD31J27CE_PRIVATE_LAB_DIRECTORY"',
            '"GD31J27CE_PARENT_LAB"',
            "Path.IsPathFullyQualified(directory)",
            'directory.StartsWith(@"\\\\", StringComparison.Ordinal)',
            "FileFlagBackupSemantics | FileFlagOpenReparsePoint",
            "FileAttributeReparsePoint",
        ):
            self.assertIn(term, self.source)
        self.assertLess(self.source.index('Environment.GetEnvironmentVariable("GD31J27CE_PARENT_LAB")'),
                        self.source.index("CreateFileW(canonical, RequiredAccessMask,"))

    def test_parent_security_reuses_held_handle_probes(self):
        for term in (
            "RecoveryWindowsParentSecurityProbe.ObserveExistingHandle(handle)",
            "RecoveryWindowsAclRiskObserver.ObserveExistingHandle(handle)",
            "RecoveryWindowsJournalScratchFactory.EvaluateParent(",
            "handle.Dispose()",
            "Interlocked.Exchange(ref _handle, null)?.Dispose()",
        ):
            self.assertIn(term, self.source)

    def test_parent_rights_are_not_trusted_ancestry(self):
        for term in (
            "ProductionOpenerEnabled => false;",
            "CanAuthorizeProductionRoot => false;",
            "HasVerifiedAncestorChain => false;",
            "HasVerifiedEffectiveAccess => false;",
            "CanAuthorizeCleanup => false;",
            "CanAuthorizeDeletion => false;",
            "CanAuthorizePublication => false;",
        ):
            self.assertIn(term, self.source)

    def test_eight_parent_mstest_cases_authored(self):
        self.assertEqual(8, self.tests.count("[TestMethod]"))
        self.assertIn("RequestedAccessIncludesAddFileTraverseReadControlAndSync", self.tests)


class HeldManifestBytesContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.codec = (OFFICE / "RecoveryHeldLabManifestCodec.cs").read_text(encoding="utf-8")
        cls.reader = (OFFICE / "RecoveryHeldJournalManifestReadback.cs").read_text(encoding="utf-8")
        cls.tests = (TESTS / "RecoveryHeldJournalManifestReadbackTests.cs").read_text(encoding="utf-8")
        cls.production = (OFFICE / "OfficeRecoveryStore.cs").read_text(encoding="utf-8")

    def test_lab_manifest_cannot_be_confused_with_production_format(self):
        self.assertIn("IsProductionManifestFormat => false;", self.codec)
        self.assertIn("HasIndependentAuthentication => false;", self.codec)
        self.assertIn("internal const int MaxManifestBytes = 1024", self.codec)
        self.assertIn("record struct RecoveryHeldLabManifestClaim(", self.codec)
        self.assertNotIn("SourcePath", self.codec.split("internal readonly record struct RecoveryHeldLabManifestClaim(")[1])

    def test_strict_bounded_canonical_json(self):
        for term in (
            "JsonDocument.Parse(encoded,",
            "MaxDepth = 4",
            "AllowTrailingCommas = false",
            "CommentHandling = JsonCommentHandling.Disallow",
            "JsonSerializer.Deserialize<RecoveryHeldLabManifestClaim>(encoded.Span)",
            "CryptographicOperations.FixedTimeEquals(",
            "TransactionId == Guid.Empty",
            "IsCanonicalSha(claim.JournalHeadSha256)",
        ):
            self.assertIn(term, self.codec)

    def test_two_reads_each_journal_and_manifest(self):
        self.assertEqual(2, self.reader.count("RecoveryHeldJournalScratchLab.ReadOnHeldStreamAsync("))
        self.assertEqual(2, self.reader.count("ReadManifestOnceAsync(heldLabManifest, cancellationToken)"))
        self.assertIn("journalFirst != journalSecond", self.reader)
        self.assertIn("manifestFirst != manifestSecond", self.reader)

    def test_held_manifest_file_id_rechecked_and_bounded(self):
        for term in (
            "FileIdIfFileStream(heldManifest)",
            "RecoveryNativeHandleIdProbe.ReadFromExistingHandle(",
            "heldManifest.ReadExactlyAsync(bytes, cancellationToken)",
            "heldManifest.Length != length",
            "RecoveryHeldLabManifestCodec.MaxManifestBytes",
            "RecoveryHeldLabManifestCodec.DecodeCandidate(bytes)",
        ):
            self.assertIn(term, self.reader)

    def test_no_restore_delete_mutate_or_production_wiring(self):
        for source in (self.codec, self.reader):
            for forbidden in (
                "File.Delete(", "File.Move(", "File.Create(", "File.Open(",
                "new FileStream(", "Path.Combine(", "Directory.Delete(",
                "SetLength(", "File.Write", "NtCreateFile(", "renameat2(",
            ):
                self.assertNotIn(forbidden, source)
        for term in (
            "ProductionManifestReaderEnabled => false;",
            "CanAuthorizeCleanup => false;",
            "HasTrustedPersistentAnchor => false;",
            "CanRestore => false;",
            "CanDelete => false;",
            "CanPublish => false;",
            "HasTrustedManifest => false;",
            "HasIndependentJournalAuthentication => false;",
            "HasVerifiedCopyContents => false;",
        ):
            self.assertIn(term, self.reader)
        self.assertNotIn("RecoveryHeldJournalManifestReadback", self.production)
        self.assertNotIn("RecoveryHeldLabManifestCodec", self.production)

    def test_thirteen_mstest_readback_cases_authored(self):
        self.assertEqual(13, self.tests.count("[TestMethod]"))
        for term in (
            "MatchingHeldBytesRemainUntrustedAndCannotAuthorizeMutations",
            "ManifestHeadCannotDifferFromActualJournal",
            "ModifiedManifestBetweenReadbacksFailsClosed",
            "ExplicitLabFileHandlesCanBeReadAndReopened",
        ):
            self.assertIn(term, self.tests)


class AcceptanceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.runner = RUNNER.read_text(encoding="utf-8")

    def test_offline_step_comes_before_restore(self):
        step = 'CommandStep("recovery-ce-windows-share-held-manifest-source-tests"'
        self.assertEqual(1, self.runner.count(step))
        self.assertLess(self.runner.index(step),
                        self.runner.index('CommandStep("solution-restore"'))
        self.assertIn('"-p", "test_recovery_ce_windows_share_held_manifest_contract.py"',
                      self.runner)

    def test_eight_new_manual_gates_no_release(self):
        gates = (
            "gd31j27ce-windows-exclusive-default-no-change-manual-e2e",
            "gd31j27ce-windows-readonly-sharing-observer-manual-e2e",
            "gd31j27ce-windows-add-file-traverse-parent-manual-e2e",
            "gd31j27ce-windows-reparse-ancestor-denial-manual-e2e",
            "gd31j27ce-windows-ntfs-no-write-delete-concurrency-manual-e2e",
            "gd31j27ce-held-journal-lab-manifest-reopen-manual-e2e",
            "gd31j27ce-smb-cross-host-share-and-checkpoint-manual-e2e",
            "gd31j27ce-worm-original-unchanged-manual-e2e",
        )
        for gate in gates:
            self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
