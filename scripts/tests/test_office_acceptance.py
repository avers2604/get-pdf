"""Unit tests for the optional, fully offline local GD-31I acceptance runner."""
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import office_acceptance as acceptance


class AcceptancePlanTests(unittest.TestCase):
    def test_gd31j27bg_private_publication_is_inactive_until_platform_proof(self):
        for host in ("Windows", "Linux"):
            steps = acceptance.build_steps(Path("/getdoc"), Path("/offline-feed"), host)
            names = [step.name for step in steps]
            source = "recovery-private-publication-source-tests"
            self.assertEqual(1, names.count(source))
            self.assertLess(names.index(source), names.index("solution-restore"))
            step = next(s for s in steps if s.name == source)
            self.assertIn("test_recovery_private_publication_contract.py", step.command)
            report = acceptance.new_report("a" * 40, host, "GD-31I")
            for manual in (
                "gd31j27bg-private-publication-windows-ntfs-manual-e2e",
                "gd31j27bg-private-publication-astra-ext4-manual-e2e",
                "gd31j27bg-private-publication-smb-manual-e2e",
            ):
                self.assertIn(manual, acceptance.MANUAL_GATES)
                self.assertEqual("not-run", report["manualAcceptance"][manual])
            self.assertFalse(report["releaseReady"])

    def test_gd31j27bf_verified_handle_staging_is_manual_and_inactive(self):
        for host in ("Windows", "Linux"):
            steps = acceptance.build_steps(Path("/getdoc"), Path("/offline-feed"), host)
            names = [step.name for step in steps]
            source = "recovery-verified-staging-source-tests"
            self.assertEqual(1, names.count(source))
            self.assertLess(names.index(source), names.index("solution-restore"))
            step = next(s for s in steps if s.name == source)
            self.assertIn("test_recovery_verified_staging_contract.py", step.command)
            report = acceptance.new_report("f" * 40, host, "GD-31I")
            for manual in (
                "gd31j27bf-verified-staging-windows-ntfs-manual-e2e",
                "gd31j27bf-verified-staging-astra-ext4-manual-e2e",
                "gd31j27bf-verified-staging-smb-cancellation-manual-e2e",
            ):
                self.assertIn(manual, acceptance.MANUAL_GATES)
                self.assertEqual("not-run", report["manualAcceptance"][manual])
            self.assertFalse(report["releaseReady"])

    def test_gd31j27be_native_probes_are_inactive_and_need_all_three_os_gates(self):
        for host in ("Windows", "Linux"):
            steps = acceptance.build_steps(Path("/getdoc"), Path("/offline-feed"), host)
            names = [step.name for step in steps]
            source = "recovery-native-probes-source-tests"
            self.assertEqual(1, names.count(source))
            self.assertLess(names.index(source), names.index("solution-restore"))
            step = next(s for s in steps if s.name == source)
            self.assertIn("test_recovery_native_probes_contract.py", step.command)

            report = acceptance.new_report("e" * 40, host, "GD-31I")
            for gate in (
                "gd31j27be-native-windows-fileid-manual-e2e",
                "gd31j27be-native-astra-openat2-manual-e2e",
                "gd31j27be-native-smb-identity-manual-e2e",
            ):
                self.assertIn(gate, acceptance.MANUAL_GATES)
                self.assertEqual("not-run", report["manualAcceptance"][gate])
            self.assertFalse(report["releaseReady"])

    def test_gd31j27bd_native_identity_is_disabled_until_os_evidence(self):
        for host in ("Windows", "Linux"):
            steps = acceptance.build_steps(Path("/getdoc"), Path("/offline-feed"), host)
            names = [step.name for step in steps]
            source = "recovery-native-identity-source-tests"
            self.assertEqual(1, names.count(source))
            self.assertLess(names.index(source), names.index("solution-restore"))
            step = next(s for s in steps if s.name == source)
            self.assertIn("test_recovery_native_identity_contract.py", step.command)

            report = acceptance.new_report("d" * 40, host, "GD-31I")
            for gate in (
                "gd31j27bd-native-identity-windows-ntfs-manual-e2e",
                "gd31j27bd-native-identity-astra-ext4-smb-manual-e2e",
            ):
                self.assertIn(gate, acceptance.MANUAL_GATES)
                self.assertEqual("not-run", report["manualAcceptance"][gate])
            self.assertFalse(report["releaseReady"])

    def test_gd31j27bc_preopen_path_swap_needs_real_os_identity_evidence(self):
        for host in ("Windows", "Linux"):
            steps = acceptance.build_steps(Path("/getdoc"), Path("/offline-feed"), host)
            names = [step.name for step in steps]
            source = "recovery-preopen-guard-source-tests"
            self.assertEqual(1, names.count(source))
            self.assertLess(names.index(source), names.index("solution-restore"))
            target = next(step for step in steps if step.name == source)
            self.assertIn("test_recovery_preopen_guard_contract.py", target.command)
            report = acceptance.new_report("c" * 40, host, "GD-31I")
            for gate in (
                "gd31j27bc-recovery-preopen-windows-ntfs-manual-e2e",
                "gd31j27bc-recovery-preopen-astra-smb-manual-e2e",
            ):
                self.assertIn(gate, acceptance.MANUAL_GATES)
                self.assertEqual("not-run", report["manualAcceptance"][gate])
            self.assertFalse(report["releaseReady"])

    def test_gd31j27bb_single_stream_needs_windows_astra_smb_runtime_proof(self):
        for host in ("Windows", "Linux"):
            steps = acceptance.build_steps(Path("/getdoc"), Path("/offline-feed"), host)
            names = [step.name for step in steps]
            source = "recovery-single-stream-source-tests"
            self.assertEqual(1, names.count(source))
            self.assertLess(names.index(source), names.index("solution-restore"))
            target = next(s for s in steps if s.name == source)
            self.assertIn("test_recovery_single_stream_contract.py", target.command)

            report = acceptance.new_report("b" * 40, host, "GD-31I")
            for gate in (
                "gd31j27bb-single-stream-windows-ntfs-manual-e2e",
                "gd31j27bb-single-stream-astra-smb-manual-e2e",
            ):
                self.assertIn(gate, acceptance.MANUAL_GATES)
                self.assertEqual("not-run", report["manualAcceptance"][gate])
            self.assertFalse(report["releaseReady"])

    def test_gd31j27ba_concurrent_manifest_and_copy_checks_need_platform_proof(self):
        for host in ("Windows", "Linux"):
            steps = acceptance.build_steps(Path("/getdoc"), Path("/offline-feed"), host)
            names = [step.name for step in steps]
            source = "recovery-concurrent-selection-source-tests"
            self.assertEqual(1, names.count(source))
            self.assertLess(names.index(source), names.index("solution-restore"))
            source_step = next(s for s in steps if s.name == source)
            self.assertIn("test_recovery_concurrent_selection_contract.py",
                          source_step.command)

            report = acceptance.new_report("a" * 40, host, "GD-31I")
            for manual in (
                "gd31j27ba-recovery-concurrent-windows-ntfs-manual-e2e",
                "gd31j27ba-recovery-concurrent-astra-smb-manual-e2e",
            ):
                self.assertIn(manual, acceptance.MANUAL_GATES)
                self.assertEqual("not-run", report["manualAcceptance"][manual])
            self.assertFalse(report["releaseReady"])

    def test_gd31j27az_fast_recovery_lookup_needs_real_os_benchmarks(self):
        for host in ("Windows", "Linux"):
            steps = acceptance.build_steps(Path("/getdoc"), Path("/offline-feed"), host)
            names = [step.name for step in steps]
            source = "recovery-fast-selection-source-tests"
            self.assertEqual(1, names.count(source))
            self.assertLess(names.index(source), names.index("solution-restore"))
            step = next(s for s in steps if s.name == source)
            self.assertIn("test_recovery_fast_selection_contract.py", step.command)

            report = acceptance.new_report("a" * 40, host, "GD-31I")
            for gate in (
                "gd31j27az-recovery-fast-selection-windows-ntfs-manual-e2e",
                "gd31j27az-recovery-fast-selection-astra-smb-manual-e2e",
            ):
                self.assertIn(gate, acceptance.MANUAL_GATES)
                self.assertEqual("not-run", report["manualAcceptance"][gate])
            self.assertFalse(report["releaseReady"])

    def test_gd31j27ay_recovery_folder_unavailable_is_not_empty_or_release_ready(self):
        for host in ("Windows", "Linux"):
            steps = acceptance.build_steps(Path("/getdoc"), Path("/offline-feed"), host)
            names = [step.name for step in steps]
            source = "recovery-folder-state-source-tests"
            self.assertEqual(1, names.count(source))
            self.assertLess(names.index(source), names.index("solution-restore"))
            target = next(step for step in steps if step.name == source)
            self.assertIn("test_recovery_folder_state_contract.py", target.command)
            report = acceptance.new_report("f" * 40, host, "GD-31I")
            for manual in (
                "gd31j27ay-recovery-folder-state-windows-ntfs-manual-e2e",
                "gd31j27ay-recovery-folder-state-astra-smb-manual-e2e",
            ):
                self.assertIn(manual, acceptance.MANUAL_GATES)
                self.assertEqual("not-run", report["manualAcceptance"][manual])
            self.assertFalse(report["releaseReady"])

    def test_gd31j27ax_corrupt_json_cleanup_and_acl_evidence_remains_manual(self):
        for host in ("Windows", "Linux"):
            steps = acceptance.build_steps(Path("/getdoc"), Path("/offline-feed"), host)
            names = [step.name for step in steps]
            check = "recovery-chain-consistency-source-tests"
            self.assertEqual(1, names.count(check))
            self.assertLess(names.index(check), names.index("solution-restore"))
            report = acceptance.new_report("e" * 40, host, "GD-31I")
            for gate in (
                "gd31j27ax-recovery-manifest-fault-windows-ntfs-manual-e2e",
                "gd31j27ax-recovery-manifest-fault-astra-smb-manual-e2e",
            ):
                self.assertIn(gate, acceptance.MANUAL_GATES)
                self.assertEqual("not-run", report["manualAcceptance"][gate])
            self.assertFalse(report["releaseReady"])

    def test_gd31j27aw_partial_deletion_requires_real_windows_and_astra_acceptance(self):
        for host in ("Windows", "Linux"):
            steps = acceptance.build_steps(Path("/getdoc"), Path("/offline-feed"), host)
            names = [step.name for step in steps]
            gate = "recovery-chain-consistency-source-tests"
            self.assertEqual(1, names.count(gate))
            self.assertLess(names.index(gate), names.index("solution-restore"))
            report = acceptance.new_report("d" * 40, host, "GD-31I")
            for name in (
                "gd31j27aw-recovery-partial-delete-windows-ntfs-manual-e2e",
                "gd31j27aw-recovery-partial-delete-astra-smb-manual-e2e",
            ):
                self.assertIn(name, acceptance.MANUAL_GATES)
                self.assertEqual("not-run", report["manualAcceptance"][name])
            self.assertFalse(report["releaseReady"])

    def test_gd31j27av_discard_integrity_needs_platform_evidence(self):
        for host in ("Windows", "Linux"):
            steps = acceptance.build_steps(Path("/getdoc"), Path("/offline-feed"), host)
            source_gate = "recovery-chain-consistency-source-tests"
            names = [step.name for step in steps]
            self.assertEqual(1, names.count(source_gate))
            self.assertLess(names.index(source_gate), names.index("solution-restore"))
            report = acceptance.new_report("c" * 40, host, "GD-31I")
            for gate in (
                "gd31j27av-recovery-discard-integrity-windows-ntfs-manual-e2e",
                "gd31j27av-recovery-discard-integrity-astra-smb-manual-e2e",
            ):
                self.assertIn(gate, acceptance.MANUAL_GATES)
                self.assertEqual("not-run", report["manualAcceptance"][gate])
            self.assertFalse(report["releaseReady"])

    def test_gd31j27au_collision_safety_remains_unverified_on_real_filesystems(self):
        for host in ("Windows", "Linux"):
            steps = acceptance.build_steps(Path("/getdoc"), Path("/offline-feed"), host)
            names = [step.name for step in steps]
            self.assertIn("recovery-chain-consistency-source-tests", names)
            self.assertLess(names.index("recovery-chain-consistency-source-tests"),
                            names.index("solution-restore"))
            report = acceptance.new_report("b" * 40, host, "GD-31I")
            for gate in (
                "gd31j27au-recovery-name-collision-windows-ntfs-manual-e2e",
                "gd31j27au-recovery-name-collision-linux-astra-manual-e2e",
            ):
                self.assertIn(gate, acceptance.MANUAL_GATES)
                self.assertEqual("not-run", report["manualAcceptance"][gate])
            self.assertFalse(report["releaseReady"])

    def test_gd31j27at_recovery_chain_checks_are_offline_and_release_remains_blocked(self):
        expected = (
            "recovery-readonly-inventory-source-tests",
            "recovery-diagnostics-ui-source-tests",
            "recovery-diagnostics-a11y-source-tests",
            "recovery-tab-lifecycle-source-tests",
            "recovery-reopen-shutdown-source-tests",
            "recovery-chain-consistency-source-tests",
        )
        for host in ("Windows", "Linux"):
            steps = acceptance.build_steps(Path("/getdoc"), Path("/offline-feed"), host)
            names = [step.name for step in steps]
            for name in expected:
                with self.subTest(host=host, name=name):
                    self.assertEqual(1, names.count(name))
                    self.assertLess(names.index(name), names.index("solution-restore"))
            check = next(step for step in steps if
                         step.name == "recovery-chain-consistency-source-tests")
            self.assertIn("test_recovery_chain_consistency.py", check.command)
            report = acceptance.new_report("a" * 40, host, "GD-31I")
            self.assertTrue(report["manualAcceptance"])
            self.assertTrue(all(state == "not-run"
                                for state in report["manualAcceptance"].values()))
            self.assertFalse(report["releaseReady"])

    def test_gd31j27as_reopen_shutdown_requires_manual_windows_astra_acceptance(self):
        for host in ("Windows", "Linux"):
            steps = acceptance.build_steps(Path("/getdoc"), Path("/offline-feed"), host)
            names = [s.name for s in steps]
            source = "recovery-reopen-shutdown-source-tests"
            self.assertIn(source, names)
            self.assertLess(names.index(source), names.index("solution-restore"))
            self.assertIn("test_recovery_reopen_shutdown_contract.py",
                          next(s for s in steps if s.name == source).command)
            report = acceptance.new_report("f" * 40, host, "GD-31I")
            for gate in (
                "gd31j27as-reopen-document-session-windows-manual-e2e",
                "gd31j27as-shutdown-long-smb-scan-astra-manual-e2e",
            ):
                self.assertIn(gate, acceptance.MANUAL_GATES)
                self.assertEqual("not-run", report["manualAcceptance"][gate])
            self.assertFalse(report["releaseReady"])

    def test_gd31j27ar_recovery_tab_lifecycle_needs_manual_windows_and_astra_checks(self):
        for host in ("Windows", "Linux"):
            steps = acceptance.build_steps(Path("/getdoc"), Path("/offline-feed"), host)
            names = [step.name for step in steps]
            lifecycle = "recovery-tab-lifecycle-source-tests"
            self.assertIn(lifecycle, names)
            self.assertLess(names.index(lifecycle), names.index("solution-restore"))
            self.assertIn(
                "test_recovery_tab_lifecycle_contract.py",
                next(s for s in steps if s.name == lifecycle).command)
            report = acceptance.new_report("e" * 40, host, "GD-31I")
            for gate in (
                "gd31j27ar-recovery-tab-cancellation-windows-manual-e2e",
                "gd31j27ar-recovery-tab-cancellation-astra-manual-e2e",
            ):
                self.assertIn(gate, acceptance.MANUAL_GATES)
                self.assertEqual("not-run", report["manualAcceptance"][gate])
            self.assertFalse(report["releaseReady"])

    def test_gd31j27aq_accessibility_regression_requires_real_windows_astra_manual_checks(self):
        for host in ("Windows", "Linux"):
            steps = acceptance.build_steps(Path("/getdoc"), Path("/offline-feed"), host)
            names = [step.name for step in steps]
            name = "recovery-diagnostics-a11y-source-tests"
            self.assertIn(name, names)
            self.assertLess(names.index(name), names.index("solution-restore"))
            self.assertIn(
                "test_recovery_diagnostics_a11y_contract.py",
                next(s for s in steps if s.name == name).command)
            report = acceptance.new_report("d" * 40, host, "GD-31I")
            for gate in (
                "gd31j27aq-recovery-keyboard-narrator-windows-manual-e2e",
                "gd31j27aq-recovery-keyboard-screenreader-astra-manual-e2e",
            ):
                self.assertIn(gate, acceptance.MANUAL_GATES)
                self.assertEqual("not-run", report["manualAcceptance"][gate])
            self.assertFalse(report["releaseReady"])

    def test_gd31j27ap_recovery_ui_is_source_gated_and_requires_manual_os_checks(self):
        for host in ("Windows", "Linux"):
            steps = acceptance.build_steps(Path("/getdoc"),
                                           Path("/offline-feed"), host)
            names = [step.name for step in steps]
            name = "recovery-diagnostics-ui-source-tests"
            self.assertIn(name, names)
            self.assertLess(names.index(name), names.index("solution-restore"))
            self.assertIn(
                "test_recovery_diagnostics_ui_contract.py",
                next(s for s in steps if s.name == name).command)
            report = acceptance.new_report("c" * 40, host, "GD-31I")
            for gate in (
                "gd31j27ap-recovery-diagnostics-windows-desktop-manual-e2e",
                "gd31j27ap-recovery-diagnostics-linux-astra-desktop-manual-e2e",
            ):
                self.assertIn(gate, acceptance.MANUAL_GATES)
                self.assertEqual("not-run", report["manualAcceptance"][gate])
            self.assertFalse(report["releaseReady"])

    def test_gd31j27ao_recovery_inspection_is_source_gated_and_not_release_ready(self):
        for host in ("Windows", "Linux"):
            steps = acceptance.build_steps(Path("/getdoc"), Path("/offline-feed"), host)
            names = [step.name for step in steps]
            name = "recovery-readonly-inventory-source-tests"
            self.assertIn(name, names)
            self.assertLess(names.index(name), names.index("solution-restore"))
            self.assertIn("test_recovery_readonly_inventory_contract.py",
                          next(step for step in steps if step.name == name).command)
            report = acceptance.new_report("b" * 40, host, "GD-31I")
            for gate in (
                "gd31j27ao-recovery-inventory-windows-ntfs-manual-e2e",
                "gd31j27ao-recovery-inventory-linux-astra-manual-e2e",
            ):
                self.assertIn(gate, acceptance.MANUAL_GATES)
                self.assertEqual("not-run", report["manualAcceptance"][gate])
            self.assertFalse(report["releaseReady"])

    def test_gd31j27al_filesystem_observation_is_not_saveas_attestation(self):
        for host in ("Windows", "Linux"):
            steps = acceptance.build_steps(Path("/disposable"), Path("/offline-feed"), host)
            names = [step.name for step in steps]
            self.assertIn("savecopy-synthetic-filesystem-tests", names)
            self.assertLess(names.index("savecopy-synthetic-filesystem-tests"),
                            names.index("solution-restore"))
            self.assertIn("test_savecopy_fs_probe.py",
                          next(step for step in steps if
                               step.name == "savecopy-synthetic-filesystem-tests").command)
            report = acceptance.new_report("a" * 40, host, "GD-31I")
            for gate in (
                "gd31j27al-windows-ntfs-real-dotnet-savecopy-manual-e2e",
                "gd31j27al-linux-ext4-real-dotnet-savecopy-manual-e2e",
                "gd31j27al-offline-smb-real-dotnet-savecopy-manual-e2e",
            ):
                self.assertIn(gate, acceptance.MANUAL_GATES)
                self.assertEqual("not-run", report["manualAcceptance"][gate])
            self.assertFalse(report["releaseReady"])

    def test_gd31j27ak_windows_pipe_denial_requires_real_manual_acceptance(self):
        linux = acceptance.build_steps(Path("/get-document"),
                                        Path("/offline-feed"), "Linux")
        windows = acceptance.build_steps(Path("C:/get-document"),
                                          Path("C:/offline-feed"), "Windows")
        for steps in (linux, windows):
            names = [s.name for s in steps]
            self.assertIn("windows-named-pipe-denial-source-tests", names)
            self.assertLess(names.index("windows-named-pipe-denial-source-tests"),
                            names.index("solution-restore"))
            self.assertIn("test_windows_named_pipe_denial_source.py",
                next(s for s in steps if s.name ==
                     "windows-named-pipe-denial-source-tests").command)
        names = [s.name for s in windows]
        self.assertIn("windows-denial-probe-restore", names)
        self.assertIn("windows-denial-probe-build", names)
        self.assertLess(names.index("windows-denial-probe-restore"),
                        names.index("windows-denial-probe-build"))
        self.assertNotIn("windows-denial-probe-build",
                         [s.name for s in linux])
        self.assertIn(
            "eurooffice-gd31j27ak-windows-currentuser-pid-denial-manual-e2e",
            acceptance.MANUAL_GATES)
        report = acceptance.new_report("a" * 40, "Windows", "GD-31I")
        self.assertEqual(
            "not-run",
            report["manualAcceptance"][
                "eurooffice-gd31j27ak-windows-currentuser-pid-denial-manual-e2e"])
        self.assertFalse(report["releaseReady"])

    def test_gd31j27ah_manual_ipc_uses_only_offline_mock_tests_before_restore(self):
        steps=acceptance.build_steps(Path("/get-document"),Path("/offline-feed"),"Linux")
        names=[step.name for step in steps]
        for name,filename in (
            ("native-saveas-manual-triparty-tests",
             "test_native_saveas_manual_triparty_devnull.py"),
            ("native-saveas-manual-triparty-source-tests",
             "test_native_saveas_manual_triparty_contract.py"),
        ):
            self.assertIn(name,names)
            self.assertLess(names.index(name),names.index("solution-restore"))
            self.assertIn(filename,next(s for s in steps if s.name==name).command)
        self.assertIn(
            "eurooffice-gd31j27ah-distinct-uid-devnull-scm-rights-manual-e2e",
            acceptance.MANUAL_GATES)
        self.assertFalse(acceptance.new_report("a"*40,"Linux","GD-31I")["releaseReady"])

    def test_gd31j27ag_offline_triparty_trace_never_marks_release(self):
        steps=acceptance.build_steps(Path("/get-document"),Path("/offline-feed"),"Linux")
        names=[step.name for step in steps]
        for name,filename in (
            ("native-saveas-three-party-trace-tests",
             "test_native_saveas_three_party_offline_trace.py"),
            ("native-saveas-three-party-trace-source-tests",
             "test_native_saveas_three_party_offline_trace_contract.py"),
        ):
            self.assertIn(name,names)
            self.assertLess(names.index(name),names.index("solution-restore"))
            self.assertIn(filename,next(s for s in steps if s.name==name).command)
        self.assertIn(
            "eurooffice-gd31j27ag-independent-triparty-denial-trace-vm-e2e",
            acceptance.MANUAL_GATES)
        self.assertFalse(acceptance.new_report("a"*40,"Linux","GD-31I")["releaseReady"])

    def test_gd31j27af_vm_fd_preopen_never_authorizes_release(self):
        steps=acceptance.build_steps(Path("/get-document"),Path("/offline-feed"),"Linux")
        names=[step.name for step in steps]
        for name,filename in (
            ("native-saveas-disposable-vm-preopen-tests",
             "test_native_saveas_disposable_vm_preopen.py"),
            ("native-saveas-disposable-vm-preopen-source-tests",
             "test_native_saveas_disposable_vm_preopen_contract.py"),
        ):
            self.assertIn(name,names)
            self.assertLess(names.index(name),names.index("solution-restore"))
            self.assertIn(filename,next(s for s in steps if s.name==name).command)
        self.assertIn(
            "eurooffice-gd31j27af-real-fixed-fd-proc-elf-readonly-e2e",
            acceptance.MANUAL_GATES)
        self.assertFalse(acceptance.new_report("a"*40,"Linux","GD-31I")["releaseReady"])

    def test_gd31j27ae_fd_origin_namespace_remains_unwired_and_denied(self):
        steps=acceptance.build_steps(Path("/get-document"),Path("/offline-feed"),"Linux")
        names=[step.name for step in steps]
        for name,filename in (
            ("native-saveas-fd-origin-namespace-tests",
             "test_native_saveas_fd_origin_namespace.py"),
            ("native-saveas-fd-origin-namespace-source-tests",
             "test_native_saveas_fd_origin_namespace_contract.py"),
        ):
            self.assertIn(name,names)
            self.assertLess(names.index(name),names.index("solution-restore"))
            self.assertIn(filename,next(s for s in steps if s.name==name).command)
        self.assertIn(
            "eurooffice-gd31j27ae-pidfd-fd-origin-namespace-elf-hash-e2e",
            acceptance.MANUAL_GATES)
        self.assertFalse(acceptance.new_report("a"*40,"Linux","GD-31I")["releaseReady"])

    def test_gd31j27ad_preopened_proc_elf_source_only_requires_vm_evidence(self):
        steps=acceptance.build_steps(Path("/get-document"),Path("/offline-feed"),"Linux")
        names=[step.name for step in steps]
        for gate,filename in (
            ("native-saveas-proc-maps-elf-tests",
             "test_native_saveas_proc_maps_elf_collector.py"),
            ("native-saveas-proc-maps-elf-source-tests",
             "test_native_saveas_proc_maps_elf_contract.py"),
        ):
            self.assertIn(gate,names)
            self.assertLess(names.index(gate),names.index("solution-restore"))
            self.assertIn(filename,next(x for x in steps if x.name==gate).command)
        self.assertIn(
            "eurooffice-gd31j27ad-preopened-proc-maps-elf64-deny-e2e",
            acceptance.MANUAL_GATES)
        self.assertFalse(acceptance.new_report("a"*40,"Linux","GD-31I")["releaseReady"])

    def test_gd31j27ac_offline_wheel_record_and_os_evidence_remain_denied(self):
        steps=acceptance.build_steps(Path("/get-document"),Path("/offline-feed"),"Linux")
        names=[step.name for step in steps]
        for gate,filename in (
            ("native-saveas-offline-vm-evidence-tests",
             "test_native_saveas_offline_vm_evidence.py"),
            ("native-saveas-offline-vm-source-tests",
             "test_native_saveas_offline_vm_evidence_contract.py"),
        ):
            self.assertIn(gate,names)
            self.assertLess(names.index(gate),names.index("solution-restore"))
            self.assertIn(filename,next(x for x in steps if x.name==gate).command)
        self.assertIn(
            "eurooffice-gd31j27ac-wheel-record-elf-systemd-os-snapshot-e2e",
            acceptance.MANUAL_GATES)
        self.assertFalse(acceptance.new_report("a"*40,"Linux","GD-31I")["releaseReady"])

    def test_gd31j27ab_signed_sbom_and_systemd_review_do_not_authorize_release(self):
        steps=acceptance.build_steps(Path("/get-document"),Path("/offline-feed"),"Linux")
        names=[step.name for step in steps]
        for gate,filename in (
            ("native-saveas-offline-sbom-evidence-tests",
             "test_native_saveas_offline_sbom_evidence.py"),
            ("native-saveas-offline-sbom-source-tests",
             "test_native_saveas_offline_sbom_contract.py"),
        ):
            self.assertIn(gate,names)
            self.assertLess(names.index(gate),names.index("solution-restore"))
            self.assertIn(filename,next(x for x in steps if x.name==gate).command)
        self.assertIn(
            "eurooffice-gd31j27ab-ed25519-sbom-effective-unit-review-e2e",
            acceptance.MANUAL_GATES)
        self.assertFalse(acceptance.new_report("a"*40,"Linux","GD-31I")["releaseReady"])

    def test_gd31j27aa_dependency_unit_pins_remain_offline_and_denied(self):
        steps=acceptance.build_steps(Path("/get-document"),Path("/offline-feed"),"Linux")
        names=[step.name for step in steps]
        for gate,filename in (
            ("native-saveas-dependency-unit-tests",
             "test_native_saveas_dependency_unit_pins.py"),
            ("native-saveas-dependency-unit-source-tests",
             "test_native_saveas_dependency_unit_pins_contract.py"),
        ):
            self.assertIn(gate,names)
            self.assertLess(names.index(gate),names.index("solution-restore"))
            self.assertIn(filename,next(x for x in steps if x.name==gate).command)
        self.assertIn(
            "eurooffice-gd31j27aa-dependency-elf-unit-pins-review-e2e",
            acceptance.MANUAL_GATES)
        self.assertFalse(acceptance.new_report("a"*40,"Linux","GD-31I")["releaseReady"])

    def test_gd31j27z_pinned_executable_and_cgroup_review_offline(self):
        steps=acceptance.build_steps(Path("/get-document"),Path("/offline-feed"),"Linux")
        names=[step.name for step in steps]
        for gate,filename in (
            ("native-saveas-launch-provenance-tests",
             "test_native_saveas_launch_provenance.py"),
            ("native-saveas-launch-provenance-source-tests",
             "test_native_saveas_launch_provenance_contract.py"),
        ):
            self.assertIn(gate,names)
            self.assertLess(names.index(gate),names.index("solution-restore"))
            self.assertIn(filename,next(x for x in steps if x.name==gate).command)
        self.assertIn(
            "eurooffice-gd31j27z-root-pinned-exe-cgroup-launch-review-e2e",
            acceptance.MANUAL_GATES)
        self.assertFalse(acceptance.new_report("a"*40,"Linux","GD-31I")["releaseReady"])

    def test_gd31j27y_fd3_challenge_remains_offline_and_denied(self):
        steps=acceptance.build_steps(Path("/get-document"),Path("/offline-feed"),"Linux")
        names=[step.name for step in steps]
        for gate,filename in (
            ("native-saveas-fd3-challenge-tests",
             "test_native_saveas_fd3_challenge.py"),
            ("native-saveas-fd3-challenge-flow-tests",
             "test_native_saveas_fd3_challenge_flow.py"),
            ("native-saveas-fd3-challenge-contract-tests",
             "test_native_saveas_fd3_challenge_contract.py"),
        ):
            self.assertIn(gate,names)
            self.assertLess(names.index(gate),names.index("solution-restore"))
            self.assertIn(filename,next(x for x in steps if x.name==gate).command)
        self.assertIn(
            "eurooffice-gd31j27y-fd3-nonce-proc-inode-rights-free-e2e",
            acceptance.MANUAL_GATES)
        self.assertFalse(acceptance.new_report("a"*40,"Linux","GD-31I")["releaseReady"])

    def test_gd31j27x_socket_peerpidfd_remains_offline(self):
        steps=acceptance.build_steps(Path("/get-document"),Path("/offline-feed"),"Linux")
        names=[step.name for step in steps]
        for gate,filename in (
            ("native-saveas-socket-peerpidfd-kernel-tests",
             "test_native_saveas_socket_peerpidfd.py"),
            ("native-saveas-socket-peerpidfd-broker-tests",
             "test_native_saveas_socket_peerpidfd_broker.py"),
            ("native-saveas-socket-peerpidfd-source-tests",
             "test_native_saveas_socket_peerpidfd_contract.py"),
        ):
            self.assertIn(gate,names)
            self.assertLess(names.index(gate),names.index("solution-restore"))
            self.assertIn(filename,next(x for x in steps if x.name==gate).command)
        self.assertIn("eurooffice-gd31j27x-socket-peerpidfd-identity-e2e",
                      acceptance.MANUAL_GATES)
        self.assertFalse(acceptance.new_report("a"*40,"Linux","GD-31I")["releaseReady"])

    def test_gd31j27w_worker_pidfd_handoff_requires_manual_vm_denial(self):
        steps=acceptance.build_steps(Path("/get-document"),Path("/offline-feed"),"Linux")
        names=[step.name for step in steps]
        for name,filename in (
            ("native-saveas-worker-pidfd-handoff-tests",
             "test_native_saveas_worker_pidfd_handoff.py"),
            ("native-saveas-worker-pidfd-source-tests",
             "test_native_saveas_worker_pidfd_handoff_contract.py"),
        ):
            self.assertIn(name,names)
            self.assertLess(names.index(name),names.index("solution-restore"))
            self.assertIn(filename,next(x for x in steps if x.name==name).command)
        self.assertIn(
            "eurooffice-gd31j27w-worker-pidfd-scm-rights-deny-e2e",
            acceptance.MANUAL_GATES)
        self.assertFalse(acceptance.new_report("a"*40,"Linux","GD-31I")["releaseReady"])

    def test_gd31j27v_pidfd_provenance_remains_offline_and_denied(self):
        steps=acceptance.build_steps(Path("/get-document"),Path("/offline-feed"),"Linux")
        names=[step.name for step in steps]
        for gate,filename in (
            ("native-saveas-broker-pidfd-tests",
             "test_native_saveas_pidfd_provenance.py"),
            ("native-saveas-broker-pidfd-sender-tests",
             "test_native_saveas_root_sender_pidfd_guard.py"),
            ("native-saveas-broker-pidfd-source-tests",
             "test_native_saveas_pidfd_source_contract.py"),
        ):
            self.assertIn(gate,names)
            self.assertLess(names.index(gate),names.index("solution-restore"))
            self.assertIn(filename,next(x for x in steps if x.name==gate).command)
        self.assertIn(
            "eurooffice-gd31j27v-pidfd-broker-liveness-identity-e2e",
            acceptance.MANUAL_GATES)
        self.assertFalse(acceptance.new_report("a"*40,"Linux","GD-31I")["releaseReady"])

    def test_gd31j27u_root_lifetime_and_dac_review_are_offline_only(self):
        steps=acceptance.build_steps(Path("/get-document"),Path("/offline-feed"),"Linux")
        names=[step.name for step in steps]
        for name,filename in (
            ("native-saveas-root-supervisor-audit-tests",
             "test_native_saveas_root_supervisor_audit.py"),
            ("native-saveas-root-supervisor-audit-source-tests",
             "test_native_saveas_root_supervisor_audit_contract.py"),
        ):
            self.assertIn(name,names)
            self.assertLess(names.index(name),names.index("solution-restore"))
            self.assertIn(filename,next(x for x in steps if x.name==name).command)
        self.assertIn(
            "eurooffice-gd31j27u-root-broker-pid-dac-namespace-audit-e2e",
            acceptance.MANUAL_GATES)
        self.assertFalse(acceptance.new_report("a"*40,"Linux","GD-31I")["releaseReady"])

    def test_gd31j27t_root_sender_is_unwired_and_offline(self):
        steps=acceptance.build_steps(Path("/get-document"),Path("/offline-feed"),"Linux")
        names=[step.name for step in steps]
        for name,filename in (
            ("native-saveas-root-supervisor-tests",
             "test_native_saveas_root_supervisor.py"),
            ("native-saveas-root-supervisor-source-tests",
             "test_native_saveas_root_supervisor_contract.py"),
        ):
            self.assertIn(name,names)
            self.assertLess(names.index(name),names.index("solution-restore"))
            self.assertIn(filename,next(x for x in steps if x.name==name).command)
        self.assertIn(
            "eurooffice-gd31j27t-root-supervisor-fixed-fd-sender-e2e",
            acceptance.MANUAL_GATES)
        self.assertFalse(acceptance.new_report("a"*40,"Linux","GD-31I")["releaseReady"])

    def test_gd31j27s_three_party_bridge_remains_offline_denied(self):
        steps=acceptance.build_steps(Path("/get-document"), Path("/offline-feed"),"Linux")
        names=[step.name for step in steps]
        for gate,filename in (
            ("native-saveas-three-party-bridge-tests",
             "test_native_saveas_three_party_bridge.py"),
            ("native-saveas-three-party-bridge-source-tests",
             "test_native_saveas_three_party_bridge_contract.py"),
        ):
            self.assertIn(gate,names)
            self.assertLess(names.index(gate),names.index("solution-restore"))
            self.assertIn(filename,next(x for x in steps if x.name==gate).command)
        self.assertIn(
            "eurooffice-gd31j27s-three-party-denial-bridge-fd-lifecycle-e2e",
            acceptance.MANUAL_GATES)
        self.assertFalse(acceptance.new_report("a"*40,"Linux","GD-31I")["releaseReady"])

    def test_gd31j27r_supervisor_fd_relay_is_offline_and_deny_only(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/offline-feed"), "Linux")
        names = [step.name for step in steps]
        for gate, filename in (
            ("native-saveas-fd-provisioning-linux-tests",
             "test_native_saveas_fd_provisioning.py"),
            ("native-saveas-fd-provisioning-source-tests",
             "test_native_saveas_fd_provisioning_contract.py"),
        ):
            self.assertIn(gate, names)
            self.assertLess(names.index(gate), names.index("solution-restore"))
            self.assertIn(filename, next(x for x in steps if x.name == gate).command)
        self.assertIn(
            "eurooffice-gd31j27r-root-supervisor-worker-owned-fd-relay-e2e",
            acceptance.MANUAL_GATES)
        self.assertFalse(acceptance.new_report("a" * 40, "Linux", "GD-31I")["releaseReady"])

    def test_gd31j27q_worker_anonymous_snapshot_remains_manual_lab_only(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/offline-feed"), "Linux")
        names = [step.name for step in steps]
        for gate, filename in (
            ("native-saveas-worker-anonymous-snapshot-linux-tests",
             "test_native_saveas_worker_snapshot.py"),
            ("native-saveas-worker-snapshot-source-tests",
             "test_native_saveas_worker_snapshot_contract.py"),
        ):
            self.assertIn(gate, names)
            self.assertLess(names.index(gate), names.index("solution-restore"))
            self.assertIn(filename,
                          next(item for item in steps if item.name == gate).command)
        self.assertIn(
            "eurooffice-gd31j27q-worker-anonymous-snapshot-fsync-linkat-crash-e2e",
            acceptance.MANUAL_GATES)
        self.assertFalse(acceptance.new_report("a" * 40, "Linux", "GD-31I")["releaseReady"])

    def test_gd31j27p_post_uid_dialer_and_listener_remain_offline_and_blocked(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/offline-feed"), "Linux")
        names = [step.name for step in steps]
        for gate, filename in (
            ("native-saveas-post-uid-dialer-tests",
             "test_native_saveas_worker_dialer.py"),
            ("native-saveas-post-uid-dialer-source-tests",
             "test_native_saveas_worker_dialer_contract.py"),
            ("native-saveas-post-uid-broker-tests",
             "test_native_saveas_broker_listener.py"),
            ("native-saveas-post-uid-broker-source-tests",
             "test_native_saveas_broker_listener_contract.py"),
        ):
            self.assertIn(gate, names)
            self.assertLess(names.index(gate), names.index("solution-restore"))
            self.assertIn(filename,
                          next(x for x in steps if x.name == gate).command)
        self.assertIn(
            "eurooffice-gd31j27p-post-uid-broker-worker-socket-lab-e2e",
            acceptance.MANUAL_GATES)
        self.assertFalse(acceptance.new_report("a" * 40, "Linux", "GD-31I")["releaseReady"])

    def test_gd31j27o_supervisor_fd3_handoff_is_offline_denial_only(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/offline-feed"), "Linux")
        names = [step.name for step in steps]
        for gate, filename in (
            ("native-saveas-supervisor-handoff-observer-tests",
             "test_native_saveas_supervisor_handoff.py"),
            ("native-saveas-supervisor-broker-guard-tests",
             "test_native_saveas_supervisor_broker_guard.py"),
            ("native-saveas-supervisor-handoff-source-tests",
             "test_native_saveas_supervisor_handoff_contract.py"),
        ):
            self.assertIn(gate, names)
            self.assertLess(names.index(gate), names.index("solution-restore"))
            self.assertIn(filename,
                          next(x for x in steps if x.name == gate).command)
        self.assertIn(
            "eurooffice-gd31j27o-supervisor-peercred-pid-start-fd3-handoff-e2e",
            acceptance.MANUAL_GATES)
        self.assertFalse(acceptance.new_report("a" * 40, "Linux", "GD-31I")["releaseReady"])

    def test_gd31j27n_kernel_observer_and_blocked_sandbox_are_deferred(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/offline-feed"), "Linux")
        names = [step.name for step in steps]
        for gate, filename in (
            ("native-saveas-kernel-sandbox-observer-tests",
             "test_native_saveas_sandbox_observer.py"),
            ("native-saveas-kernel-sandbox-observer-source-tests",
             "test_native_saveas_sandbox_observer_contract.py"),
        ):
            self.assertIn(gate, names)
            self.assertLess(names.index(gate), names.index("solution-restore"))
            self.assertIn(filename,
                          next(x for x in steps if x.name == gate).command)
        self.assertIn(
            "eurooffice-gd31j27n-live-linux-kernel-sandbox-observer-e2e",
            acceptance.MANUAL_GATES)
        self.assertFalse(acceptance.new_report("a" * 40, "Linux", "GD-31I")["releaseReady"])

    def test_gd31j27m_broker_and_profile_are_offline_manual_only(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/offline-feed"), "Linux")
        names = [step.name for step in steps]
        for gate, file in (
            ("native-saveas-fd-broker-protocol-tests",
             "test_native_saveas_fd_broker.py"),
            ("native-saveas-broker-profile-contract-tests",
             "test_native_saveas_broker_profile.py"),
            ("native-saveas-fd-broker-source-contract-tests",
             "test_native_saveas_fd_broker_contract.py"),
        ):
            self.assertIn(gate, names)
            self.assertLess(names.index(gate), names.index("solution-restore"))
            self.assertIn(file, next(s for s in steps if s.name == gate).command)
        self.assertIn(
            "eurooffice-gd31j27m-external-broker-fd-handoff-isolation-e2e",
            acceptance.MANUAL_GATES)
        self.assertFalse(acceptance.new_report("a" * 40, "Linux", "GD-31I")["releaseReady"])

    def test_gd31j27l_fd_only_worker_is_deny_only_and_requires_vm_isolation(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/approved-feed"), "Linux")
        names = [item.name for item in steps]
        for gate, testfile in (
            ("native-saveas-fd-worker-offline-protocol-tests",
             "test_native_saveas_fd_worker.py"),
            ("native-saveas-fd-worker-source-contract-tests",
             "test_native_saveas_fd_worker_contract.py"),
        ):
            self.assertIn(gate, names)
            self.assertLess(names.index(gate), names.index("solution-restore"))
            self.assertIn(testfile,
                          next(x for x in steps if x.name == gate).command)
        self.assertIn("eurooffice-gd31j27l-distinct-uid-fd3-worker-isolation-e2e",
                      acceptance.MANUAL_GATES)
        self.assertFalse(acceptance.new_report("a" * 40, "Linux", "GD-31I")["releaseReady"])

    def test_gd31j27k_anonymous_snapshot_owner_gate_requires_linux_vm(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/approved-offline-feed"), "Linux")
        names = [item.name for item in steps]
        for gate, testfile in (
            ("native-saveas-unlinked-snapshot-source-contract-tests",
             "test_native_unlinked_snapshot_contract.py"),
            ("native-saveas-unlinked-snapshot-linux-tests",
             "test_native_unlinked_snapshot_linux.py"),
        ):
            self.assertIn(gate, names)
            self.assertLess(names.index(gate), names.index("solution-restore"))
            self.assertIn(testfile, next(x for x in steps if x.name == gate).command)
        self.assertIn(
            "eurooffice-gd31j27k-owner-only-unlinked-snapshot-same-uid-writer-e2e",
            acceptance.MANUAL_GATES)

    def test_gd31j27j_fd_bound_linux_link_and_manual_race_gate_are_deferred(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/approved-feed"), "Linux")
        names = [step.name for step in steps]
        for gate, testfile in (
            ("native-saveas-fd-bound-link-source-contract-tests",
             "test_native_fd_bound_link_contract.py"),
            ("native-saveas-fd-bound-link-linux-regression-tests",
             "test_native_fd_bound_link_linux.py"),
        ):
            self.assertIn(gate, names)
            self.assertLess(names.index(gate), names.index("solution-restore"))
            self.assertIn(testfile, next(s for s in steps if s.name == gate).command)
        self.assertIn("eurooffice-gd31j27j-fd-bound-link-no-path-swap-e2e",
                      acceptance.MANUAL_GATES)

    def test_gd31j27i_evidence_gate_is_local_deferred_and_never_release(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        for gate, testfile in (
            ("native-saveas-offline-acceptance-evidence-tests",
             "test_native_saveas_acceptance_gate.py"),
            ("native-saveas-offline-acceptance-source-contract-tests",
             "test_native_saveas_acceptance_gate_contract.py"),
        ):
            self.assertIn(gate, names)
            self.assertLess(names.index(gate), names.index("solution-restore"))
            self.assertIn(testfile,
                          next(x for x in steps if x.name == gate).command)
        report = acceptance.new_report("a" * 40, "Linux", "GD-31I")
        self.assertFalse(report["releaseReady"])
        self.assertFalse(report["automatedChecksPassed"])
        self.assertTrue(all(s == "not-run"
                            for s in report["manualAcceptance"].values()))

    def test_gd31j27h_complete_native_lineage_and_crash_review_remain_manual(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [item.name for item in steps]
        gate = "native-saveas-complete-chain-reconciliation-source-tests"
        self.assertIn(gate, names)
        self.assertLess(names.index(gate), names.index("solution-restore"))
        self.assertIn("test_native_saveas_chain_reconciliation.py",
                      next(step for step in steps if step.name == gate).command)
        self.assertIn("eurooffice-gd31j27h-v1-v2-native-ooxml-crash-lineage-e2e",
                      acceptance.MANUAL_GATES)

    def test_gd31j27g_durable_no_replace_and_crash_orphan_review_are_deferred(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        for gate, filename in (
            ("native-saveas-durable-policy-source-contract-tests",
             "test_native_saveas_durable_policy_contract.py"),
            ("native-saveas-orphan-read-only-inventory-tests",
             "test_native_saveas_orphan_inventory.py"),
        ):
            self.assertIn(gate, names)
            self.assertLess(names.index(gate), names.index("solution-restore"))
            self.assertIn(filename,
                          next(item for item in steps if item.name == gate).command)
        self.assertIn("eurooffice-gd31j27g-fsync-no-replace-crash-recovery-e2e",
                      acceptance.MANUAL_GATES)

    def test_gd31j27f_bounded_native_callback_close_is_deferred_and_manual(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        target = "pinned-saveas-event-budget-lifecycle-source-contract-tests"
        self.assertIn(target, names)
        self.assertLess(names.index(target), names.index("solution-restore"))
        self.assertIn("test_native_saveas_event_budget_contract.py",
                      next(x for x in steps if x.name == target).command)
        self.assertIn("eurooffice-pinned-v2-bounded-callback-shutdown-operator-ui-e2e",
                      acceptance.MANUAL_GATES)

    def test_gd31j27e_state_machine_negative_matrix_is_offline_and_manual(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        for gate, script in (
            ("pinned-saveas-state-negative-matrix-tests",
             "test_native_pinned_saveas_state_model.py"),
            ("pinned-saveas-state-source-contract-tests",
             "test_native_pinned_saveas_state_source_contract.py"),
        ):
            self.assertIn(gate, names)
            self.assertLess(names.index(gate), names.index("solution-restore"))
            self.assertIn(script, next(step for step in steps
                                       if step.name == gate).command)
        self.assertIn("eurooffice-pinned-v2-state-machine-early-duplicate-cancel-timeout-e2e",
                      acceptance.MANUAL_GATES)

    def test_gd31j27d_native_v2_supervisor_is_offline_with_separate_vm_gate(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        for gate, script in (
            ("pinned-native-v2-supervisor-tests", "test_native_pinned_v2_supervisor.py"),
            ("pinned-native-v2-supervisor-source-contract-tests",
             "test_native_pinned_v2_supervisor_contract.py"),
        ):
            self.assertIn(gate, names)
            self.assertLess(names.index(gate), names.index("solution-restore"))
            self.assertIn(script, next(item for item in steps
                                       if item.name == gate).command)
        self.assertIn("eurooffice-pinned-v2-supervised-native-host-and-runtime-shas-e2e",
                      acceptance.MANUAL_GATES)

    def test_gd31j27c_pinned_v2_keeps_native_telemetry_offline_and_manual(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        for gate, test in (
            ("pinned-native-saveas-event-v2-tests", "test_native_pinned_saveas_event_v2.py"),
            ("pinned-native-saveas-event-v2-source-contract-tests",
             "test_native_pinned_saveas_event_v2_contract.py"),
            ("pinned-native-event-v2-ooxml-join-tests",
             "test_native_pinned_event_ooxml_join.py"),
        ):
            self.assertIn(gate, names)
            self.assertLess(names.index(gate), names.index("solution-restore"))
            self.assertIn(test, next(x for x in steps if x.name == gate).command)
        self.assertIn("eurooffice-pinned-native-v2-callback-counts-and-ooxml-join-e2e",
                      acceptance.MANUAL_GATES)

    def test_gd31j27b_protocol_diagnostics_remain_offline_and_manual(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [item.name for item in steps]
        for gate, file in (
            ("native-saveas-protocol-reconciliation-tests",
             "test_native_saveas_protocol_reconciliation.py"),
            ("native-saveas-protocol-reconciliation-source-contract-tests",
             "test_native_saveas_protocol_reconciliation_contract.py"),
        ):
            self.assertIn(gate, names)
            self.assertLess(names.index(gate), names.index("solution-restore"))
            self.assertIn(file, next(item for item in steps
                                     if item.name == gate).command)
        self.assertIn("eurooffice-v1-v2-saveas-protocol-lineage-reconciliation-e2e",
                      acceptance.MANUAL_GATES)

    def test_gd31j27a_supervised_native_receipt_is_deferred_and_fail_closed(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        for gate, file in (
            ("native-ooxml-supervisor-tests", "test_native_ooxml_supervisor.py"),
            ("native-ooxml-supervisor-source-contract-tests",
             "test_native_ooxml_supervisor_contract.py"),
        ):
            self.assertIn(gate, names)
            self.assertLess(names.index(gate), names.index("solution-restore"))
            self.assertIn(file, next(step for step in steps
                                     if step.name == gate).command)
        self.assertIn("eurooffice-supervised-native-saveas-pin-stdout-and-ooxml-e2e",
                      acceptance.MANUAL_GATES)

    def test_gd31j27_native_ooxml_correlator_requires_offline_tests_and_manual_vm(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        for gate, file in (
            ("native-ooxml-join-tests", "test_native_ooxml_join.py"),
            ("native-ooxml-join-source-contract-tests", "test_native_ooxml_join_contract.py"),
        ):
            self.assertIn(gate, names)
            self.assertLess(names.index(gate), names.index("solution-restore"))
            step = next(item for item in steps if item.name == gate)
            self.assertIn(file, step.command)
        self.assertIn("eurooffice-native-ooxml-session-sha-and-canary-join-e2e",
                      acceptance.MANUAL_GATES)

    def test_gd31j26_native_saveas_event_bridge_is_only_a_manual_isolated_lab(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        self.assertIn("cef-native-saveas-source-contract-tests", names)
        self.assertLess(names.index("cef-native-saveas-source-contract-tests"),
                        names.index("solution-restore"))
        check = next(step for step in steps
                     if step.name == "cef-native-saveas-source-contract-tests")
        self.assertIn("test_native_saveas_contract.py", check.command)
        self.assertIn("eurooffice-native-saveas-pinned-sdk-output-isolation-e2e",
                      acceptance.MANUAL_GATES)

    def test_gd31j25_offline_verifier_runs_before_restore_and_native_save_is_manual(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        self.assertIn("synthetic-ooxml-roundtrip-verifier-tests", names)
        self.assertLess(names.index("synthetic-ooxml-roundtrip-verifier-tests"),
                        names.index("solution-restore"))
        check = next(item for item in steps
                     if item.name == "synthetic-ooxml-roundtrip-verifier-tests")
        self.assertIn("test_synthetic_roundtrip.py", check.command)
        self.assertIn("eurooffice-synthetic-ooxml-native-saveas-canary-e2e",
                      acceptance.MANUAL_GATES)

    def test_patched_native_ready_sdk_requires_two_offline_source_gates_and_manual_vm(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        for name in ("cef-native-ready-overlay-tests", "cef-native-ready-bridge-source-tests"):
            self.assertIn(name, names)
            self.assertLess(names.index(name), names.index("solution-restore"))
        self.assertIn("eurooffice-content-ready-patched-sdk-runtime-e2e",
                      acceptance.MANUAL_GATES)

    def test_converter_candidate_contract_requires_manual_synthetic_cef_e2e(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        self.assertIn("cef-native-conversion-evidence-source-tests", names)
        self.assertLess(names.index("cef-native-conversion-evidence-source-tests"),
                        names.index("solution-restore"))
        self.assertIn("eurooffice-native-converter-private-artifact-candidate-e2e",
                      acceptance.MANUAL_GATES)

    def test_cef_binding_receipt_source_gate_precedes_offline_restore(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        self.assertIn("cef-native-binding-source-contract-tests", names)
        self.assertLess(names.index("cef-native-binding-source-contract-tests"),
                        names.index("solution-restore"))
        self.assertIn("eurooffice-native-sdk-fixture-binding-and-import-e2e",
                      acceptance.MANUAL_GATES)

    def test_native_save_receipt_contract_is_in_local_offline_preflight(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Windows")
        names = [step.name for step in steps]
        self.assertIn("office-native-save-receipt-contract-tests", names)
        self.assertLess(names.index("office-native-save-receipt-contract-tests"),
                        names.index("solution-restore"))
        step = next(s for s in steps
                    if s.name == "office-native-save-receipt-contract-tests")
        self.assertIn("test_lo_save_receipt_contract.py", step.command)

    def test_native_optimistic_save_and_no_overwrite_copy_require_manual_gate(self):
        self.assertIn("office-native-save-conflict-and-no-overwrite-copy",
                      acceptance.MANUAL_GATES)


    def test_only_local_feed_used_and_all_suites_enforced(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/approved/feed"), "Windows")
        restore = next(step for step in steps if step.name == "solution-restore")
        self.assertEqual(
            ["dotnet", "restore", "GetDocument.slnx", "--source", "/approved/feed",
             "-p:NuGetAudit=false"],
            restore.command)
        for item in steps:
            if item.command[:2] == ["dotnet", "restore"]:
                self.assertIn("-p:NuGetAudit=false", item.command)
        self.assertEqual(1, len([s for s in steps if s.name == "solution-build"]))
        self.assertTrue(any(s.name == "windows-print-worker-build" for s in steps))
        suites = [s for s in steps if s.name.startswith("test-")]
        self.assertEqual(set(acceptance.TEST_PROJECTS.values()) | {"tests/GetPdf.Tests/GetPdf.Tests.csproj"},
                         {s.project for s in suites})
        for step in suites:
            self.assertIn("--minimum-expected-tests", step.command)
            self.assertIn("--no-restore", step.command)
        self.assertNotIn("http", " ".join(" ".join(step.command) for step in steps).lower())

    def test_eurooffice_stacked_regression_gate_runs_before_restore(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        self.assertIn("eurooffice-stacked-contract-integrity-tests", names)
        self.assertLess(names.index("eurooffice-stacked-contract-integrity-tests"),
                        names.index("solution-restore"))
        step = next(s for s in steps
                    if s.name == "eurooffice-stacked-contract-integrity-tests")
        self.assertIn("test_eurooffice_stacked_contract_integrity.py", step.command)

    def test_avalonia_ime_caret_source_contract_is_in_deferred_acceptance(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        self.assertIn("avalonia-ime-caret-source-contract-tests", names)
        self.assertLess(names.index("avalonia-ime-caret-source-contract-tests"),
                        names.index("solution-restore"))
        step = next(s for s in steps if s.name == "avalonia-ime-caret-source-contract-tests")
        self.assertIn("test_avalonia_ime_caret_contract.py", step.command)

    def test_ime_commit_source_contract_is_in_deferred_acceptance(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        self.assertIn("cef-osr-ime-commit-source-contract-tests", names)
        self.assertLess(names.index("cef-osr-ime-commit-source-contract-tests"),
                        names.index("solution-restore"))
        step = next(s for s in steps if s.name == "cef-osr-ime-commit-source-contract-tests")
        self.assertIn("test_osr_ime_commit_contract.py", step.command)

    def test_cef_keyboard_lab_contract_is_in_deferred_acceptance(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        self.assertIn("cef-osr-keyboard-source-contract-tests", names)
        self.assertLess(names.index("cef-osr-keyboard-source-contract-tests"),
                        names.index("solution-restore"))
        check = next(s for s in steps if s.name == "cef-osr-keyboard-source-contract-tests")
        self.assertIn("test_osr_keyboard_contract.py", check.command)

    def test_cef_click_drag_contract_is_in_deferred_acceptance(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        self.assertIn("cef-osr-click-source-contract-tests", names)
        self.assertLess(names.index("cef-osr-click-source-contract-tests"),
                        names.index("solution-restore"))
        check = next(step for step in steps
                     if step.name == "cef-osr-click-source-contract-tests")
        self.assertIn("test_osr_click_contract.py", check.command)

    def test_cef_pointer_input_contract_is_in_deferred_acceptance(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        self.assertIn("cef-osr-input-source-contract-tests", names)
        self.assertLess(names.index("cef-osr-input-source-contract-tests"),
                        names.index("solution-restore"))
        check = next(step for step in steps
                     if step.name == "cef-osr-input-source-contract-tests")
        self.assertIn("test_osr_input_contract.py", check.command)

    def test_cef_osr_stream_contract_is_in_deferred_acceptance(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        self.assertIn("cef-osr-stream-source-contract-tests", names)
        self.assertLess(names.index("cef-osr-stream-source-contract-tests"),
                        names.index("solution-restore"))
        step = next(x for x in steps if x.name == "cef-osr-stream-source-contract-tests")
        self.assertIn("test_osr_stream_contract.py", step.command)

    def test_real_wayland_cef_osr_contract_remains_opt_in_and_deferred(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        self.assertIn("cef-wayland-osr-source-contract-tests", names)
        self.assertLess(names.index("cef-wayland-osr-source-contract-tests"),
                        names.index("solution-restore"))
        self.assertIn("test_osr_capture_contract.py",
            next(x for x in steps if x.name == "cef-wayland-osr-source-contract-tests").command)

    def test_native_frame_transport_contract_is_deferred_to_local_acceptance(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [s.name for s in steps]
        self.assertIn("native-frame-shell-contract-tests", names)
        self.assertLess(names.index("native-frame-shell-contract-tests"),
                        names.index("solution-restore"))
        check = next(s for s in steps if s.name == "native-frame-shell-contract-tests")
        self.assertIn("test_native_frame_shell_contract.py", check.command)

    def test_native_session_dispatch_contract_is_checked_before_restore(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        self.assertIn("native-session-dispatch-contract-tests", names)
        self.assertLess(names.index("native-session-dispatch-contract-tests"),
                        names.index("solution-restore"))
        step = next(s for s in steps if s.name == "native-session-dispatch-contract-tests")
        self.assertIn("test_native_session_dispatch_contract.py", step.command)

    def test_native_avalonia_tabs_have_deferred_source_contract(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [s.name for s in steps]
        self.assertIn("native-avalonia-tab-contract-tests", names)
        self.assertLess(names.index("native-avalonia-tab-contract-tests"),
                        names.index("solution-restore"))
        contract = next(s for s in steps if s.name == "native-avalonia-tab-contract-tests")
        self.assertIn("test_native_tab_shell_contract.py", contract.command)

    def test_qt_ipc_static_contract_is_checked_without_running_qt(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [item.name for item in steps]
        self.assertIn("qt-ipc-contract-tests", names)
        self.assertLess(names.index("qt-ipc-contract-tests"),
                        names.index("solution-restore"))
        step = next(item for item in steps if item.name == "qt-ipc-contract-tests")
        self.assertIn("test_qt_ipc_contract.py", step.command)

    def test_native_sdk_event_contract_will_be_checked_in_local_acceptance(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [item.name for item in steps]
        self.assertIn("native-event-contract-tests", names)
        self.assertLess(names.index("native-event-contract-tests"),
                        names.index("solution-restore"))
        step = next(item for item in steps if item.name == "native-event-contract-tests")
        self.assertIn("test_native_event_contract.py", step.command)

    def test_qt_display_evidence_validator_is_checked_before_dotnet(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        self.assertIn("qt-display-evidence-tests", names)
        self.assertLess(names.index("qt-display-evidence-tests"),
                        names.index("solution-restore"))
        evidence = next(item for item in steps if item.name == "qt-display-evidence-tests")
        self.assertNotIn("--display-live", evidence.command)
        self.assertIn("test_qt_display_evidence.py", evidence.command)

    def test_empty_view_source_contract_is_checked_offline_and_never_runs_cef(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        self.assertIn("cef-empty-view-contract-tests", names)
        self.assertLess(names.index("cef-empty-view-contract-tests"),
                        names.index("solution-restore"))
        step = next(s for s in steps if s.name == "cef-empty-view-contract-tests")
        self.assertNotIn("--cef-empty-view-lab", step.command)
        self.assertIn("test_empty_view_contract.py", step.command)

    def test_cef_lab_preflight_suite_runs_before_any_office_restore(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        self.assertIn("cef-lab-offline-tests", names)
        self.assertLess(names.index("cef-lab-offline-tests"),
                        names.index("solution-restore"))

    def test_native_probe_test_double_runs_before_dotnet_restore(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Windows")
        names = [step.name for step in steps]
        self.assertIn("native-probe-mock-tests", names)
        self.assertLess(names.index("native-probe-mock-tests"),
                        names.index("solution-restore"))

    def test_compiled_qt_host_is_a_distinct_optional_gate(self):
        qt_binary = Path("/approved/build/getdoc-eurooffice-qt-probe")
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux",
                                       qt_host_binary=qt_binary)
        checks = [step for step in steps if step.name == "native-qt-headless-smoke"]
        self.assertEqual(1, len(checks))
        self.assertEqual(str(qt_binary), checks[0].command[-1])
        self.assertLess([step.name for step in steps].index("native-qt-headless-smoke"),
                        [step.name for step in steps].index("solution-restore"))
        display = [step for step in steps if step.name == "native-qt-display-offscreen"]
        self.assertEqual(1, len(display))
        self.assertIn("test_qt_display.py", display[0].command)
        self.assertEqual(str(qt_binary), display[0].command[-1])
        ipc = [step for step in steps if step.name == "native-qt-tab-ipc"]
        self.assertEqual(1, len(ipc))
        self.assertIn("test_qt_ipc.py", ipc[0].command)
        self.assertEqual(str(qt_binary), ipc[0].command[-1])
        self.assertLess([step.name for step in steps].index("native-qt-tab-ipc"),
                        [step.name for step in steps].index("solution-restore"))

    def test_linux_plan_does_not_assume_windows_only_build_tools(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        self.assertFalse(any(step.name.startswith("windows-") for step in steps))
        self.assertFalse(any("GetPdf.slnx" in step.command for step in steps))
        self.assertIn("solution-build", [s.name for s in steps])

    def test_automatic_failure_blocks_all_following_steps(self):
        steps = [
            acceptance.CommandStep("restore", ["dotnet", "restore", "GetDocument.slnx"]),
            acceptance.CommandStep("build", ["dotnet", "build", "GetDocument.slnx"]),
            acceptance.CommandStep("test", ["dotnet", "test", "x.csproj"])
        ]
        invoked = []

        def invoke(step):
            invoked.append(step.name)
            return 23 if step.name == "build" else 0

        results, passed = acceptance.run_steps(steps, invoke)
        self.assertFalse(passed)
        self.assertEqual(["restore", "build"], invoked)
        self.assertEqual(["passed", "failed", "not-run"], [s["status"] for s in results])

    def test_refuses_dirty_worktree_so_git_sha_identifies_tested_sources(self):
        with patch.object(acceptance.subprocess, "run", side_effect=[
            SimpleNamespace(stdout="a" * 40 + "\n"),
            SimpleNamespace(stdout=" M src/GetDocument.Office/LibreOffice/lo_editor.py\n")
        ]):
            with self.assertRaises(RuntimeError):
                acceptance.git_revision(Path("/repo"))

    def test_rejects_preview_sdk_for_release_gate(self):
        with patch.object(acceptance.shutil, "which", return_value="/bin/dotnet"):
            with patch.object(acceptance.subprocess, "run",
                              return_value=SimpleNamespace(stdout="10.0.100-preview.1\n")):
                with self.assertRaises(RuntimeError):
                    acceptance.ensure_dotnet10()

    def test_empty_plan_is_not_an_automatic_success(self):
        results, passed = acceptance.run_steps([], lambda _: 0)
        self.assertEqual([], results)
        self.assertFalse(passed)

    def test_report_always_requires_manual_acceptance(self):
        report = acceptance.new_report("abc123", "Windows", "GD-31I")
        report["automatedChecksPassed"] = True
        report["status"] = "automated-checks-passed"
        self.assertFalse(report["releaseReady"])
        self.assertTrue(all(value == "not-run" for value in report["manualAcceptance"].values()))


class SourceChecksTests(unittest.TestCase):
    def test_python_whitelist_and_csharp_catalog_match_exactly(self):
        catalog = 'new("calc-one", "one", "calc", true),\nnew("writer-two", "two", "writer", true)'
        python = 'FEATURES = {\n    "calc-one": ("calc", True),\n    "writer-two": ("writer", True),\n}\n'
        self.assertEqual(["calc-one", "writer-two"], acceptance.compare_feature_catalogs(catalog, python))
        with self.assertRaises(ValueError):
            acceptance.compare_feature_catalogs(catalog, python.replace("writer-two", "hidden-feature"))
        with self.assertRaises(ValueError):
            acceptance.compare_feature_catalogs(catalog, python.replace('    "writer-two": ("writer", True),', ''))

    def test_invalid_embedded_python_fails_preflight(self):
        with self.assertRaises(SyntaxError):
            acceptance.check_python_syntax("def bad(:\n  pass")

    def test_runner_config_requires_microsoft_testing_platform(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "global.json").write_text('{"test":{"runner":"VSTest"}}', encoding="utf-8")
            with self.assertRaises(ValueError):
                acceptance.check_test_runner(root)
            (root / "global.json").write_text(
                '{"test":{"runner":"Microsoft.Testing.Platform"}}', encoding="utf-8")
            acceptance.check_test_runner(root)

    def test_report_evidence_is_never_overwritten(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "existing-report.json"
            path.write_text('{"original":"approved-evidence"}', encoding="utf-8")
            with self.assertRaises(FileExistsError):
                acceptance.write_report(path, acceptance.new_report("abc", "Windows", "GD-31I"))
            self.assertEqual('{"original":"approved-evidence"}',
                             path.read_text(encoding="utf-8"))

    def test_json_report_is_atomic_and_has_no_release_approval(self):
        with tempfile.TemporaryDirectory() as temp:
            report = acceptance.new_report("012345", "Linux", "GD-31I")
            path = Path(temp) / "report.json"
            acceptance.write_report(path, report)
            saved = json.loads(path.read_text(encoding="utf-8"))
            self.assertFalse(saved["releaseReady"])
            self.assertEqual("012345", saved["revision"])
            self.assertFalse(path.with_suffix(".json.tmp").exists())


if __name__ == "__main__":
    unittest.main()
