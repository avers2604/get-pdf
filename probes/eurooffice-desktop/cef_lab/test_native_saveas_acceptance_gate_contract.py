"""GD31J27i: offline-only source contracts for independent review gate.

The gate must remain read-only and NEVER become a SaveAs/merge authorization
based on JSON flags or a successful stdout receipt.
"""
from pathlib import Path
import sys
import unittest

LAB = Path(__file__).resolve().parent
ROOT = LAB.parents[2]
sys.path.insert(0, str(LAB))
sys.path.insert(0, str(ROOT / "scripts"))
import native_saveas_acceptance_gate as gate
import office_acceptance as acceptance


class NativeSaveAsAcceptanceSourceContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.gate = (LAB / "native_saveas_acceptance_gate.py").read_text("utf-8")
        cls.cmake = (LAB / "CMakeLists.txt").read_text("utf-8")
        cls.runner = (ROOT / "scripts/office_acceptance.py").read_text("utf-8")
        cls.policy = (LAB / "native_saveas_policy.h").read_text("utf-8")

    def test_all_native_stages_are_explicit_and_nonoptional_manual_gates(self):
        self.assertEqual(37, len(gate.REQUIRED_MANUAL_GATES))
        self.assertEqual(len(gate.REQUIRED_MANUAL_GATES),
                         len(set(gate.REQUIRED_MANUAL_GATES)))
        self.assertTrue(set(gate.REQUIRED_MANUAL_GATES).issubset(
            acceptance.MANUAL_GATES))
        self.assertIn("eurooffice-gd31j27h-v1-v2-native-ooxml-crash-lineage-e2e",
                      gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27j-fd-bound-link-no-path-swap-e2e",
                      gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27k-owner-only-unlinked-snapshot-same-uid-writer-e2e",
                      gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27l-distinct-uid-fd3-worker-isolation-e2e",
                      gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27m-external-broker-fd-handoff-isolation-e2e",
                      gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27n-live-linux-kernel-sandbox-observer-e2e",
                      gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27o-supervisor-peercred-pid-start-fd3-handoff-e2e",
                      gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27p-post-uid-broker-worker-socket-lab-e2e",
                      gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27q-worker-anonymous-snapshot-fsync-linkat-crash-e2e",
                      gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27r-root-supervisor-worker-owned-fd-relay-e2e",
                      gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27s-three-party-denial-bridge-fd-lifecycle-e2e",
                      gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27t-root-supervisor-fixed-fd-sender-e2e", gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27u-root-broker-pid-dac-namespace-audit-e2e", gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27v-pidfd-broker-liveness-identity-e2e", gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27w-worker-pidfd-scm-rights-deny-e2e", gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27x-socket-peerpidfd-identity-e2e", gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27y-fd3-nonce-proc-inode-rights-free-e2e", gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27z-root-pinned-exe-cgroup-launch-review-e2e", gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27aa-dependency-elf-unit-pins-review-e2e", gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27ab-ed25519-sbom-effective-unit-review-e2e", gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27ac-wheel-record-elf-systemd-os-snapshot-e2e", gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27ad-preopened-proc-maps-elf64-deny-e2e", gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27ae-pidfd-fd-origin-namespace-elf-hash-e2e", gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27af-real-fixed-fd-proc-elf-readonly-e2e", gate.REQUIRED_MANUAL_GATES)

    def test_all_fault_scenarios_are_required_and_machine_readable(self):
        self.assertEqual(40, len(gate.CASES))
        self.assertEqual(len(gate.CASES), len(set(gate.CASES)))
        for fault in ("source-name-swap-during-linkat",
                      "file-fsync-denied-before-linkat",
                      "directory-fsync-denied-after-linkat",
                      "process-killed-after-linkat-before-dir-fsync",
                      "unrecognized-files-symlinks-never-deleted"):
            self.assertIn(fault, gate.CASES)
        self.assertIn("same-uid-writer-after-anonymous-snapshot", gate.CASES)
        self.assertIn("worker-fd-only-distinct-uid-mount-net-seccomp", gate.CASES)
        self.assertIn("broker-peercred-one-shot-fd-handoff-no-privileges", gate.CASES)
        self.assertIn("worker-live-proc-status-namespace-mountinfo-caps-deny", gate.CASES)
        self.assertIn("supervisor-peercred-pid-start-exact-fd3-table", gate.CASES)
        self.assertIn("post-uid-broker-bind-worker-connect-fd3-peercred", gate.CASES)
        self.assertIn("worker-anonymous-snapshot-crash-fsync-linkat-unknown-outcome", gate.CASES)
        self.assertIn("root-supervisor-scm-rights-worker-owned-fd-relay-deny", gate.CASES)
        self.assertIn("three-party-root-broker-worker-session-fd-close-denial", gate.CASES)
        self.assertIn("root-fixed-manifest-synthetic-fd-sender-no-extra-dac", gate.CASES)
        self.assertIn("root-broker-pid-reuse-dac-caps-mountns-deny", gate.CASES)
        self.assertIn("broker-pidfd-exit-poll-unsupported-kernel-no-fallback", gate.CASES)
        self.assertIn("worker-pidfd-death-before-after-scm-rights-no-replay", gate.CASES)
        self.assertIn("socket-peerpidfd-kernel-connected-worker-identity-no-fallback", gate.CASES)
        self.assertIn("fd3-nonce-inode-rights-free-no-proven-executable", gate.CASES)
        self.assertIn("root-pinned-exe-cgroup-argv-uid-sha-no-launch-authority", gate.CASES)
        self.assertIn("pinned-python-elf-so-unit-import-closure-no-unknown-loads", gate.CASES)
        self.assertIn("detached-ed25519-sbom-key-rotation-digest-mismatch-unit-dropin-deny", gate.CASES)
        self.assertIn("wheel-record-archive-and-os-observation-mismatch-deny", gate.CASES)
        self.assertIn("preopened-proc-maps-elf-needed-fd-origin-failclosed", gate.CASES)
        self.assertIn("pidfd-fd-target-namespace-swap-elf-sha-no-launch-authority", gate.CASES)
        self.assertIn("disposable-vm-proc-fixed-preopen-path-symlink-fd-cleanup-deny", gate.CASES)
        self.assertIn("REQUIRED_AUTOMATED_STEPS", self.gate)

    def test_even_all_claims_cannot_enable_production_or_release(self):
        for mark in ('"releaseReady": False',
                     '"approvedForProduction": False',
                     '"writePermissionGranted": False',
                     '"safeToDeleteAutomatically": False',
                     '"nativeSaveProven": False',
                     '"documentVerified": False',
                     '"saveVerified": False'):
            self.assertIn(mark, self.gate)
        self.assertIn('return 2', self.gate)
        self.assertNotIn("subprocess.", self.gate)
        self.assertNotIn("os.unlink(", self.gate)
        self.assertNotIn("os.replace(", self.gate)
        self.assertNotIn("shutil.rmtree(", self.gate)
        self.assertNotIn("open(path, 'w')", self.gate)

    def test_opt_in_native_build_remains_default_off(self):
        self.assertIn(
            'option(GETDOC_GD31J26_ENABLE_NATIVE_SAVEAS_LAB '
            '"Isolated synthetic SaveAs via exact vendor event" OFF)', self.cmake)
        self.assertIn(
            'option(GETDOC_GD31J27C_ENABLE_NATIVE_EVENT_V2_LAB '
            '"Pinned native SaveAs event v2 diagnostic for isolated synthetic VM" OFF)',
            self.cmake)
        self.assertIn("GETDOC_GD31J27C_EVENT_V2_ISOLATION_CONFIRMED", self.cmake)
        self.assertIn("GETDOC_GD31J26_PRIVATE_OUTPUT_ISOLATION_CONFIRMED", self.cmake)
        self.assertIn("NativeSaveAsPublish(", self.policy)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
