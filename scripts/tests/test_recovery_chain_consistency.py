"""GD-31J.27at: stacked recovery-contract integrity checks (source-only).

These tests guard future regressions in ordering and registration; they are
NOT .NET compilation, GUI automation, or Windows/Astra release evidence.
"""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
PANE = ROOT / "src/GetDocument.Desktop/Recovery/RecoveryDiagnosticsPane.cs"
WINDOW = ROOT / "src/GetDocument.Desktop/MainWindow.cs"
STORE = ROOT / "src/GetDocument.Office/LibreOffice/OfficeRecoveryStore.cs"
LINK_TESTS = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryStoreLinkSafetyTests.cs"
STORE_TESTS = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryStoreTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"
TESTS = ROOT / "scripts/tests"
REQUIRED = {
    "recovery-readonly-inventory-source-tests":
        "test_recovery_readonly_inventory_contract.py",
    "recovery-diagnostics-ui-source-tests":
        "test_recovery_diagnostics_ui_contract.py",
    "recovery-diagnostics-a11y-source-tests":
        "test_recovery_diagnostics_a11y_contract.py",
    "recovery-tab-lifecycle-source-tests":
        "test_recovery_tab_lifecycle_contract.py",
    "recovery-reopen-shutdown-source-tests":
        "test_recovery_reopen_shutdown_contract.py",
    "recovery-chain-consistency-source-tests":
        "test_recovery_chain_consistency.py",
    "recovery-folder-state-source-tests":
        "test_recovery_folder_state_contract.py",
    "recovery-fast-selection-source-tests":
        "test_recovery_fast_selection_contract.py",
    "recovery-concurrent-selection-source-tests":
        "test_recovery_concurrent_selection_contract.py",
    "recovery-single-stream-source-tests":
        "test_recovery_single_stream_contract.py",
    "recovery-preopen-guard-source-tests":
        "test_recovery_preopen_guard_contract.py",
    "recovery-native-identity-source-tests":
        "test_recovery_native_identity_contract.py",
    "recovery-native-probes-source-tests":
        "test_recovery_native_probes_contract.py",
    "recovery-verified-staging-source-tests":
        "test_recovery_verified_staging_contract.py",
    "recovery-private-publication-source-tests":
        "test_recovery_private_publication_contract.py",
    "recovery-consumer-transfer-source-tests":
        "test_recovery_consumer_transfer_contract.py",
    "recovery-owned-consumer-source-tests":
        "test_recovery_owned_consumer_contract.py",
    "recovery-lifecycle-audit-source-tests":
        "test_recovery_lifecycle_audit_contract.py",
    "recovery-orphan-inventory-source-tests":
        "test_recovery_orphan_inventory_contract.py",
    "recovery-orphan-budget-native-delete-source-tests":
        "test_recovery_orphan_budget_native_delete_contract.py",
    "recovery-capture-volume-source-tests":
        "test_recovery_capture_volume_contract.py",
    "recovery-volume-identity-source-tests":
        "test_recovery_volume_identity_contract.py",
}


class RecoveryChainIntegrity(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.pane = PANE.read_text(encoding="utf-8")
        cls.window = WINDOW.read_text(encoding="utf-8")
        cls.store = STORE.read_text(encoding="utf-8")
        cls.link_tests = LINK_TESTS.read_text(encoding="utf-8")
        cls.store_tests = STORE_TESTS.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")
        cls.refresh = cls.pane.split(
            "internal async Task RefreshAsync()", 1
        )[1].split("private void Render(", 1)[0]
        cls.finally_body = cls.refresh.split("finally\n        {", 1)[1]

    def test_dispose_child_linked_token_before_parent_on_shutdown(self):
        # Using declarations are disposed only when the method exits, AFTER
        # its finally. Disposing the shared parent inside that finally first
        # breaks the intended linked-token lifetime ordering.
        self.assertNotIn("using var inspection =", self.refresh)
        self.assertIn("var inspection = CancellationTokenSource.CreateLinkedTokenSource(",
                      self.refresh)
        self.assertIn("inspection.Dispose();", self.finally_body)
        self.assertIn("DisposeLifetime();", self.finally_body)
        self.assertLess(self.finally_body.index("inspection.Dispose();"),
                        self.finally_body.index("DisposeLifetime();"))
        self.assertIn("if (ReferenceEquals(_activeInspection, inspection))",
                      self.finally_body)
        self.assertIn("_activeInspection = null;", self.finally_body)

    def test_cancelled_or_closed_scan_never_publishes_stale_results(self):
        self.assertIn("if (_disposed || _suspended || token.IsCancellationRequested) return;",
                      self.refresh)
        self.assertIn("_activeInspection?.Cancel();", self.pane)
        self.assertIn("_lifetime.Cancel();", self.pane)
        self.assertNotIn(".Wait();", self.refresh)
        self.assertNotIn("Task.WaitAll(", self.refresh)

    def test_every_recovery_source_contract_is_registered_once_before_restore(self):
        for name, file_name in REQUIRED.items():
            with self.subTest(name=name):
                self.assertTrue((TESTS / file_name).is_file())
                self.assertEqual(1, self.runner.count('CommandStep("' + name + '"'))
                self.assertLess(self.runner.index('CommandStep("' + name + '"'),
                                self.runner.index('CommandStep("solution-restore"'))
                self.assertIn('"-p", "' + file_name + '"', self.runner)

    def test_registered_recovery_manual_gates_are_unique_and_not_automated(self):
        all_gates = re.findall(r'    "((?:gd31j27a[lnopqrstuvwxyz]|gd31j27b[abcdefghijklmn])[^"]+-manual-e2e)",',
                               self.runner)
        self.assertEqual(len(all_gates), len(set(all_gates)))
        self.assertGreaterEqual(len(all_gates), 8)
        self.assertIn("def new_report(", self.runner)
        self.assertIn('"manualAcceptance": {name: "not-run" for name in MANUAL_GATES}', self.runner)
        self.assertIn('"releaseReady": False', self.runner)

    def test_no_retired_close_api_assertion_in_active_recovery_tests(self):
        for file_name in (
            "test_recovery_diagnostics_ui_contract.py",
            "test_recovery_diagnostics_a11y_contract.py",
            "test_recovery_tab_lifecycle_contract.py",
            "test_recovery_reopen_shutdown_contract.py",
        ):
            with self.subTest(file_name=file_name):
                content = (TESTS / file_name).read_text(encoding="utf-8")
                self.assertNotIn('self.assertIn("CloseRecoveryDiagnostics(id);"', content)
        self.assertIn("CloseRecoveryDiagnostics(id, restoreSourceSelection: false);",
                      self.window)

    def test_old_ui_contract_uses_per_inspection_token_not_disposed_parent(self):
        legacy = (TESTS / "test_recovery_diagnostics_ui_contract.py").read_text(
            encoding="utf-8")
        self.assertNotIn('CancellationToken token = _lifetime.Token;', legacy)
        self.assertIn('CancellationToken token = inspection.Token;', legacy)
        self.assertIn('CreateLinkedTokenSource(', self.pane)

    def test_capture_failure_never_deletes_exporter_names_without_native_ownership(self):
        capture = self.store.split(
            "public async Task<OfficeRecoveryCopy> CaptureAsync(", 1
        )[1].split("public OfficeRecoveryIncompleteInventory InspectIncomplete(", 1)[0]
        self.assertIn("await exportCopy(temporaryCopy);", capture)
        self.assertIn("File.Move(temporaryCopy, finalCopy);", capture)
        self.assertIn("File.Move(temporaryManifest, finalManifest);", capture)
        self.assertNotIn("TryDelete(temporaryCopy);", capture)
        self.assertNotIn("TryDelete(temporaryManifest);", capture)
        self.assertNotIn("TryDelete(finalCopy);", capture)
        self.assertNotIn("File.Delete(", capture)
        self.assertNotIn("Directory.Delete(", capture)
        self.assertIn("Read-only inventory can report", capture)
        self.assertLess(capture.index("await exportCopy(temporaryCopy);"),
                        capture.index("File.Move(temporaryCopy, finalCopy);"))
        self.assertLess(capture.index("File.Move(temporaryCopy, finalCopy);"),
                        capture.index("File.Move(temporaryManifest, finalManifest);"))

    def test_collision_regressions_cover_all_publication_names(self):
        for name in (
            "PreexistingFinalCopyCollisionMustNotDeleteUnrelatedFile",
            "PreexistingRegularTemporaryManifestCollisionMustNotDeleteIt",
            "PreexistingFinalManifestCollisionDoesNotPublishOrDeleteIt",
        ):
            with self.subTest(name=name):
                self.assertIn(name, self.link_tests)
        self.assertEqual(10, self.link_tests.count("[TestMethod]"))

    def test_discard_verifies_snapshot_hash_and_package_before_removal(self):
        discard = self.store.split("public void Discard(", 1)[1].split(
            "private OfficeRecoveryCopy? ReadValidCopy(", 1
        )[0]
        verify = "OfficeRecoveryCopy? verified = ReadValidCopy(source, extension, manifest);"
        self.assertIn(verify, discard)
        self.assertIn("if (verified is null) continue;", discard)
        pair_call = "DeleteVerifiedPair(verified.FilePath, manifest, TryDeleteVerifiedPart);"
        self.assertIn(pair_call, discard)
        self.assertLess(discard.index(verify), discard.index(pair_call))
        self.assertLess(discard.index("if (verified is null) continue;"),
                        discard.index(pair_call))
        self.assertNotIn("RecoveryManifest? metadata = ReadManifest(manifest);", discard)
        self.assertNotIn("Path.Combine(folder, name + extension)", discard)

    def test_discard_regressions_cover_corrupt_orphan_and_linked_artifacts(self):
        for name in (
            "DiscardRetainsTamperedSnapshotAndManifestForManualInvestigation",
            "DiscardDoesNotUnlinkSymlinkReplacedRecoveryCopy",
            "DiscardPreservesOrphanManifestWhenItsCopyDisappeared",
            "CancelledExporterLeavesUntrustedCopyForReadOnlyInventory",
        ):
            with self.subTest(name=name):
                self.assertIn(name, self.store_tests)
        self.assertIn("DiscardPreservesSymlinkedManifestAndValidSiblingCopy",
                      self.link_tests)

    def test_capture_has_last_cancellation_check_before_manifest_commit(self):
        capture = self.store.split(
            "public async Task<OfficeRecoveryCopy> CaptureAsync(", 1
        )[1].split("public OfficeRecoveryIncompleteInventory InspectIncomplete(", 1)[0]
        marker = "cancellationToken.ThrowIfCancellationRequested();"
        last_check = capture.rindex(marker)
        commit = capture.index("File.Move(temporaryManifest, finalManifest);")
        self.assertLess(capture.index("IsLinkedFile(new FileInfo(temporaryManifest))"),
                        last_check)
        self.assertLess(last_check, commit)
        self.assertIn("publishedManifest = true;", capture[commit:])

    def test_prune_and_discard_delete_verified_copy_before_manifest(self):
        discard = self.store.split("public void Discard(", 1)[1].split(
            "private OfficeRecoveryCopy? ReadValidCopy(", 1)[0]
        prune = self.store.split("private void Prune(", 1)[1].split(
            "private static void RejectLinkedDirectories(", 1)[0]
        helper = self.store.split("internal static bool DeleteVerifiedPair(", 1)[1].split(
            "private static bool TryDeleteVerifiedPart(", 1)[0]
        self.assertIn("DeleteVerifiedPair(verified.FilePath, manifest, TryDeleteVerifiedPart);",
                      discard)
        self.assertIn("DeleteVerifiedPair(entry.Copy!.FilePath, entry.Manifest, TryDeleteVerifiedPart);",
                      prune)
        self.assertIn("if (!tryDelete(copyPath)) return false;", helper)
        self.assertIn("return tryDelete(manifestPath);", helper)
        self.assertLess(helper.index("tryDelete(copyPath)"),
                        helper.index("tryDelete(manifestPath)"))
        self.assertNotIn("TryDelete(manifest);", discard)
        self.assertNotIn("TryDelete(entry.Manifest);", prune)

    def test_verified_deletion_rejects_missing_or_linked_parts(self):
        delete = self.store.split("private static bool TryDeleteVerifiedPart(", 1)[1].split(
            "internal static void TryDelete(", 1)[0]
        self.assertIn("if (!file.Exists || IsLinkedFile(file)", delete)
        self.assertIn("FileAttributes.ReparsePoint", delete)
        self.assertIn("return false;", delete)
        self.assertIn("File.Delete(path);", delete)
        self.assertIn("return !File.Exists(path);", delete)
        self.assertIn("IOException or UnauthorizedAccessException", delete)

    def test_failure_injection_regressions_cover_both_deletion_order_outcomes(self):
        tests = (ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryDeletionOrderTests.cs"
                 ).read_text(encoding="utf-8")
        for case in (
            "CopyDeletionDeniedKeepsManifestAndNeverCallsManifestDelete",
            "ManifestDeletionDeniedOnlyAfterSuccessfulCopyDeletion",
            "BothDeletionsSucceedInCopyFirstOrder",
            "FaultingCopyDeletionDoesNotAttemptManifestDeletion",
        ):
            with self.subTest(case=case):
                self.assertIn(case, tests)
        self.assertEqual(4, tests.count("[TestMethod]"))

    def test_manifest_and_copy_security_denials_are_isolated_per_candidate(self):
        read_manifest = self.store.split(
            "private static RecoveryManifest? ReadManifest(", 1
        )[1].split("private static void EnsureValidPackage(", 1)[0]
        read_copy = self.store.split("private OfficeRecoveryCopy? ReadValidCopy(", 1
                                    )[1].split("private void Prune(", 1)[0]
        for label, method in (("json", read_manifest), ("copy", read_copy)):
            with self.subTest(label=label):
                self.assertIn("System.Security.SecurityException", method)
                self.assertIn("return null;", method)
        self.assertIn("JsonException", read_manifest)
        self.assertIn("NotSupportedException", read_copy)
        self.assertIn("if (IsLinkedFile(info) || info.Length > 2048) return null;",
                      read_manifest)

    def test_temporary_cleanup_is_best_effort_for_expected_denials_only(self):
        cleanup = self.store.split("internal static void TryDelete(", 1)[1].split(
            "private sealed record RecoveryManifest(", 1
        )[0]
        for marker in (
            "Action<string>? deleteFile = null",
            "if (deleteFile is null) File.Delete(path);",
            "else deleteFile(path);",
            "IOException or UnauthorizedAccessException",
            "System.Security.SecurityException",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, "internal static void TryDelete(" + cleanup)
        self.assertNotIn("catch (Exception)", cleanup)
        self.assertNotIn("catch {", cleanup)

    def test_json_and_cleanup_regressions_are_written_without_real_user_files(self):
        for case in (
            "FindLatestSkipsMalformedAndOversizedJsonAndKeepsValidCopy",
            "FindLatestFallsBackToOlderValidCopyWhenNewerIsCorrupted",
        ):
            with self.subTest(case=case):
                self.assertIn(case, self.store_tests)
        test_file = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryCleanupFailureTests.cs"
        tests = test_file.read_text(encoding="utf-8")
        for case in (
            "IoCleanupDenialDoesNotReplacePrimaryExportFailure",
            "AccessDeniedCleanupDoesNotReplacePrimaryExportFailure",
            "SecurityPolicyCleanupDenialIsBestEffort",
            "UnexpectedProgrammingErrorsAreNotSilentlyIgnored",
        ):
            with self.subTest(case=case):
                self.assertIn(case, tests)
        self.assertEqual(4, tests.count("[TestMethod]"))
        self.assertIn("InternalsVisibleTo", (ROOT /
            "src/GetDocument.Office/GetDocument.Office.csproj").read_text(encoding="utf-8"))

    def test_no_new_user_data_operations_in_diagnostics_pane(self):
        for forbidden in (
            "File.Delete(", "File.Move(", "File.Read", "File.Write",
            "Directory.Delete(", "Directory.CreateDirectory(", "CaptureAsync(",
            "Discard(", "Restore(", "Process.Start(",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.pane)
        self.assertEqual(1, self.pane.count("readonly Button "))


if __name__ == "__main__":
    unittest.main()
