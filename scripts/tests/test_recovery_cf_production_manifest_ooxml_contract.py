"""GD-31J.27cf — source-only held production RecoveryManifest + OOXML review.

This module only inspects repository source contracts. It does not compile
.NET, run native I/O, examine production data or authorize restoration.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
OFFICE = ROOT / "src/GetDocument.Office/LibreOffice"
TESTS = ROOT / "tests/GetDocument.Office.Tests"
RUNNER = ROOT / "scripts/office_acceptance.py"


class ProductionManifestHeldSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.codec = (OFFICE / "RecoveryHeldProductionManifestCodec.cs").read_text(encoding="utf-8")
        cls.reader = (OFFICE / "RecoveryHeldProductionOoxmlReader.cs").read_text(encoding="utf-8")
        cls.store = (OFFICE / "OfficeRecoveryStore.cs").read_text(encoding="utf-8")
        cls.codec_tests = (TESTS / "RecoveryHeldProductionManifestCodecTests.cs").read_text(encoding="utf-8")
        cls.reader_tests = (TESTS / "RecoveryHeldProductionOoxmlReaderTests.cs").read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")

    def test_existing_manifest_shape_is_preserved(self):
        self.assertIn("private sealed record RecoveryManifest(string SourcePath, DateTimeOffset CapturedUtc, string Sha256);", self.store)
        self.assertIn("record struct RecoveryHeldProductionManifestCandidate(", self.codec)
        self.assertIn("string SourcePath,", self.codec)
        self.assertIn("DateTimeOffset CapturedUtc,", self.codec)
        self.assertIn("string Sha256);", self.codec)
        self.assertNotIn("TransactionId", self.codec)
        self.assertNotIn("JournalHeadSha256", self.codec)

    def test_manifest_rejects_noncanonical_and_unknown_fields(self):
        for term in (
            "MaxManifestBytes = 2048",
            "JsonDocument.Parse(bytes,",
            "MaxDepth = 4",
            "AllowTrailingCommas = false",
            "CommentHandling = JsonCommentHandling.Disallow",
            "JsonSerializer.Deserialize<RecoveryHeldProductionManifestCandidate>(",
            "JsonSerializer.SerializeToUtf8Bytes(claim)",
            "CryptographicOperations.FixedTimeEquals(canonical, bytes.Span)",
        ):
            self.assertIn(term, self.codec)

    def test_manifest_does_not_open_source_path_or_grant_rights(self):
        for term in (
            "HasTrustedManifestOrigin => false;",
            "CanOpenSourceDocument => false;",
            "CanAuthorizeRestore => false;",
            "CanAuthorizeDelete => false;",
            "claim.SourcePath.Length > 1024",
            "claim.CapturedUtc.Offset != TimeSpan.Zero",
        ):
            self.assertIn(term, self.codec)

    def test_actual_copy_is_hashed_using_held_file_stream_only(self):
        for term in (
            "ReadOnHeldStreamsAsync(",
            "Stream heldCopy, Stream heldProductionManifest,",
            "heldCopy.Position = 0;",
            "SHA256.HashDataAsync(heldCopy, cancellationToken)",
            "RecoveryNativeHandleIdProbe.ReadFromExistingHandle(",
            "heldCopy.Length != length",
        ):
            self.assertIn(term, self.reader)
        self.assertEqual(
            2, self.reader.count("SHA256.HashDataAsync(heldCopy, cancellationToken)"))

    def test_manifest_source_path_is_text_only_never_an_open_instruction(self):
        self.assertIn("firstManifest.Claim.SourcePath, expectedSourcePath", self.reader)
        self.assertIn("StringComparison.Ordinal", self.reader)
        self.assertIn("CanOpenManifestSourcePath => false;", self.reader)
        for forbidden in (
            "File.Open(", "File.OpenRead(", "File.OpenHandle(",
            "Path.GetFullPath(", "Path.Combine(", "new FileStream(",
        ):
            self.assertNotIn(forbidden, self.reader)
            self.assertNotIn(forbidden, self.codec)

    def test_two_independent_held_streams_required(self):
        for term in (
            "ReferenceEquals(heldCopy, heldProductionManifest)",
            "(heldCopy is FileStream) != (heldProductionManifest is FileStream)",
            "!heldCopy.CanRead || !heldCopy.CanSeek",
            "!heldProductionManifest.CanRead || !heldProductionManifest.CanSeek",
            "firstCopy.NativeFileId == firstManifest.NativeFileId",
            "firstCopy.NativeFileId.DeviceOrVolume !=",
        ):
            self.assertIn(term, self.reader)

    def test_two_complete_passes_manifest_and_copy(self):
        self.assertEqual(2, self.reader.count("ReadManifestAsync(")-1)
        self.assertEqual(2, self.reader.count("ReadCopyAsync(")-1)
        for term in (
            "firstManifest != secondManifest || firstCopy != secondCopy",
            "firstManifest.Claim.Sha256, firstCopy.Sha256",
            "string.Equals(firstSha, secondSha, StringComparison.Ordinal)",
            "FileIdIfHeldFile(heldManifest)",
            "FileIdIfHeldFile(heldCopy)",
        ):
            self.assertIn(term, self.reader)

    def test_zip_limits_match_existing_recovery_guard(self):
        for term in (
            "MaxCopyBytes = 128L * 1024 * 1024",
            "MaxEntryBytes = 128L * 1024 * 1024",
            "MaxExpandedBytes = 512L * 1024 * 1024",
            "MaxEntryCount = 4096",
            "MaxCompressionRatio = 500",
            "new ZipArchive(openedCopy, ZipArchiveMode.Read",
            "ZipArchiveEntry entry in archive.Entries",
        ):
            self.assertIn(term, self.reader)
        for term in (
            "MaxCopyBytes = 128L * 1024 * 1024",
            "MaxEntryBytes = 128L * 1024 * 1024",
            "MaxExpandedBytes = 512L * 1024 * 1024",
            "MaxEntryCount = 4096",
            "MaxCompressionRatio = 500",
        ):
            self.assertIn(term, self.store)

    def test_zip_rejects_traversal_duplicates_symlink_macros_activex(self):
        for term in (
            "HashSet<string>(StringComparer.OrdinalIgnoreCase)",
            "name.Contains('\\\\')",
            "name.Contains(':')",
            "name.Any(char.IsControl)",
            's is "." or ".."',
            "!names.Add(name.TrimEnd('/'))",
            "((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000",
            '"/vbaProject.bin"',
            '"activeX/"',
            '"/activeX/"',
        ):
            self.assertIn(term, self.reader)

    def test_xml_rejects_dtd_and_limits_expansion(self):
        for term in (
            "DtdProcessing = DtdProcessing.Prohibit",
            "XmlResolver = null",
            "MaxCharactersInDocument = MaxEntryBytes",
            "expanded > MaxExpandedBytes - entry.Length",
            "entry.Length > entry.CompressedLength * (long)MaxCompressionRatio",
            "entry.Length > MaxEntryBytes",
            'names.Contains("[Content_Types].xml")',
            "names.Contains(mainPart)",
        ):
            self.assertIn(term, self.reader)

    def test_lab_reader_has_no_file_mutation_or_authority(self):
        forbidden = (
            "File.Delete(", "File.Move(", "File.Create(", "File.Open(",
            "Directory.Delete(", "Directory.CreateDirectory(",
            "NtCreateFile(", "unlinkat(", "renameat2(", "SetLength(",
            "File.Write", "NtSetInformationFile(", "MoveFileExW(",
        )
        for source in (self.codec, self.reader):
            for term in forbidden:
                with self.subTest(term=term):
                    self.assertNotIn(term, source)
        for term in (
            "ProductionReaderEnabled => false;",
            "CanAuthorizeCleanup => false;",
            "CanRestore => false;",
            "CanDelete => false;",
            "CanPublish => false;",
            "HasIndependentManifestAuthentication => false;",
            "HasVerifiedNameBinding => false;",
            "HasVerifiedDurability => false;",
        ):
            self.assertIn(term, self.reader)
        self.assertNotIn("RecoveryHeldProductionOoxmlReader", self.store)
        self.assertNotIn("RecoveryHeldProductionManifestCodec", self.store)

    def test_eight_manifest_and_eighteen_ooxml_tests_authored(self):
        self.assertEqual(8, self.codec_tests.count("[TestMethod]"))
        self.assertEqual(18, self.reader_tests.count("[TestMethod]"))
        for name in (
            "MatchesExistingProductionManifestJsonShape",
            "UnknownFieldIsRejectedRatherThanIgnored",
            "ValidProductionDocxManifestAndCopyHashMatchOnlyAtObservation",
            "ChangedCopyBetweenPassesRejected",
            "ChangedManifestBetweenPassesRejected",
            "VirtualCopyOver128MiBIsRejectedBeforeHashing",
            "DuplicateCaseInsensitiveZipPartIsRejected",
        ):
            self.assertIn(name, self.codec_tests + self.reader_tests)

    def test_no_worm_recovery_or_actions_gates_modified(self):
        self.assertIn('"releaseReady": False', self.runner)
        self.assertNotIn("OfficeRecoveryStore.EnsureValidPackage =", self.reader)

    def test_manual_gates_and_offline_step_registered(self):
        gates = (
            "gd31j27cf-production-manifest-held-json-manual-e2e",
            "gd31j27cf-copy-held-sha256-dual-pass-manual-e2e",
            "gd31j27cf-docx-xlsx-pptx-zip-guard-manual-e2e",
            "gd31j27cf-copy-hash-manifest-mismatch-manual-e2e",
            "gd31j27cf-xml-dtd-zip-bomb-rejection-manual-e2e",
            "gd31j27cf-windows-ntfs-smb-race-manual-e2e",
            "gd31j27cf-astra-ext4-held-reopen-manual-e2e",
            "gd31j27cf-worm-original-immutability-manual-e2e",
        )
        for gate in gates:
            self.assertIn(gate, self.runner)
        step = 'CommandStep("recovery-cf-production-manifest-ooxml-source-tests"'
        self.assertEqual(1, self.runner.count(step))
        self.assertLess(self.runner.index(step),
                        self.runner.index('CommandStep("solution-restore"'))
        self.assertIn('"-p", "test_recovery_cf_production_manifest_ooxml_contract.py"', self.runner)


if __name__ == "__main__":
    unittest.main()
