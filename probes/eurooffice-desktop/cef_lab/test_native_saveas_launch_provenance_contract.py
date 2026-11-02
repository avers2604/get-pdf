"""GD31J27z deferred source-only launch provenance policy checks.

Read-only tests do NOT confer root-trust, executable-signature attestation,
Astra MAC approval, BPF verification or permission to publish documents.
"""
from pathlib import Path
import unittest

LAB=Path(__file__).resolve().parent
ROOT=LAB.parents[2]

class LaunchProvenanceSourceContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.policy=(LAB/"native_saveas_launch_provenance.py").read_text("utf8")
        cls.broker=(LAB/"native_saveas_broker_listener.py").read_text("utf8")
        cls.worker=(LAB/"native_saveas_fd_worker.py").read_text("utf8")
        cls.flow=(LAB/"native_saveas_fd3_challenge_flow.py").read_text("utf8")
        cls.cmake=(LAB/"CMakeLists.txt").read_text("utf8")
        cls.acceptance=(ROOT/"scripts/office_acceptance.py").read_text("utf8")

    def test_fixed_root_owned_manifest_and_hashes_never_approve(self):
        for needle in (
            'FIXED_MANIFEST="/etc/getdoc-lab/gd31j27z-launch-pins.json"',
            'os.O_NOFOLLOW','exact_mode=0o600',
            'SCRIPT_PATHS={','CGROUP_PATHS={',
            'def parse_pinned_manifest(','object_pairs_hook=_unique_pairs',
            'def load_root_owned_manifest(',
            'def inspect_snapshot(','def inspect_live_process(',
            'def inspect_pair(',
            'pidfd.pin_broker_process(',
            'os.readlink(f"/proc/{pid}/exe")',
            'os.open(f"/proc/{pid}/exe"',
            '_hash_bounded_fd(fd)',
            '_hash_fixed_source(',
            '"cgroup":_read_proc(pid,"cgroup")',
            '"cmdline":_read_proc(pid,"cmdline")',
            'audit.read_broker_start_ticks(pid)',
            '"trustedRootManifestAttested":False',
            '"workerExecutableAttested":False',
            '"brokerExecutableAttested":False',
            '"nativePublicationAuthorized":False',
            '"releaseReady":False',
            '"approvedForProduction":False',
        ):
            with self.subTest(needle=needle):
                self.assertIn(needle,self.policy)

    def test_no_production_entrypoint_or_privilege_bypass(self):
        for name in (
            "native_saveas_launch_provenance",
            "load_root_owned_manifest(",
            "inspect_pair(",
        ):
            for source in (self.broker,self.worker,self.flow,self.cmake):
                self.assertNotIn(name,source)
        for forbidden in (
            "subprocess.", "os.setuid(", "os.setgid(", "os.chmod(",
            "os.chown(", "os.unlink(", "os.rename(", "os.replace(",
            "os.link(", "os.fork(", "os.execv(", "os.setns(",
            "os.unshare(", "os.kill(", "pidfd_getfd(", "socket.socket(",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden,self.policy)
        self.assertIn("GD31J27Z_BLOCKED:",self.cmake)
        self.assertIn("native-saveas-launch-provenance-source-tests",
                      self.acceptance)

if __name__=="__main__":
    unittest.main()
