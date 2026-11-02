"""GD31J27s source-level integration restrictions; no runtime sandbox proof.

These tests are deferred until disposable offline Astra Linux lab approval.
"""
from pathlib import Path
import unittest

LAB=Path(__file__).resolve().parent
ROOT=LAB.parents[2]


class ThreePartyBridgeSourceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.bridge=(LAB/"native_saveas_three_party_bridge.py").read_text("utf8")
        cls.listener=(LAB/"native_saveas_broker_listener.py").read_text("utf8")
        cls.worker=(LAB/"native_saveas_fd_worker.py").read_text("utf8")
        cls.provision=(LAB/"native_saveas_fd_provisioning.py").read_text("utf8")
        cls.snapshot=(LAB/"native_saveas_worker_snapshot.py").read_text("utf8")
        cls.cmake=(LAB/"CMakeLists.txt").read_text("utf8")
        cls.acceptance=(ROOT/"scripts/office_acceptance.py").read_text("utf8")

    def test_explicit_bridge_reuses_kernel_reviewed_single_use_fd_relay(self):
        for needle in (
            "def bridge_inspect_only_once(", "provisioning.relay_pinned_once(",
            "session=broker.BrokerSession()", "session.consumed=True",
            'data["mode"] != "inspect_only"', "allowPublication",
            '"nativePublicationAuthorized":False',
            '"nativeSaveProven":False',
            '"documentVerified":False',
            '"saveVerified":False',
            '"releaseReady":False',
            '"approvedForProduction":False',
            "def _exact_denial(", "result != expected_upstream_denial_for_contract()",
        ):
            with self.subTest(token=needle):
                self.assertIn(needle,self.bridge)

    def test_broker_listener_default_remains_no_fds_and_no_bridge_invocation(self):
        self.assertIn("def bridge_accepted_diagnostic_only(",self.listener)
        self.assertIn("three_party.bridge_inspect_only_once(",self.listener)
        head=self.listener.split("def bridge_accepted_diagnostic_only(")[0]
        tail=self.listener.split("def main(argv=None):")[1]
        self.assertIn("channel.sendall(",head)
        self.assertNotIn("relay_pinned_once(",head)
        self.assertNotIn("bridge_accepted_diagnostic_only(",tail)
        self.assertNotIn("native_saveas_three_party_bridge",self.worker)
        self.assertNotIn("native_saveas_three_party_bridge",self.snapshot)
        self.assertNotIn("native_saveas_three_party_bridge",self.cmake)
        self.assertNotIn("publish_disposable_fixture(",self.bridge)

    def test_no_privileged_launch_or_document_paths(self):
        for forbidden in (
            "subprocess", "os.open(", "os.setuid(", "os.setgid(",
            "os.mount(", "os.unshare(", "os.unlink(", "os.rename(",
            "os.replace(", "os.link(", "shutil.", "Popen(",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden,self.bridge)
        self.assertIn('GD31J27S_BLOCKED:',self.cmake)
        self.assertIn("native-saveas-three-party-bridge-source-tests",self.acceptance)


if __name__=="__main__":
    unittest.main()
