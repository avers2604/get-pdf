"""GD31J27u deferred source contracts for root/broker lifetime and DAC audit."""
from pathlib import Path
import unittest

LAB=Path(__file__).resolve().parent
ROOT=LAB.parents[2]

class RootBrokerAuditSourceContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.audit=(LAB/"native_saveas_root_supervisor_audit.py").read_text("utf8")
        cls.sender=(LAB/"native_saveas_root_supervisor.py").read_text("utf8")
        cls.broker=(LAB/"native_saveas_broker_listener.py").read_text("utf8")
        cls.worker=(LAB/"native_saveas_fd_worker.py").read_text("utf8")
        cls.unit=(LAB/"getdoc-gd31j27t-root-supervisor.blocked.service").read_text("utf8")
        cls.cmake=(LAB/"CMakeLists.txt").read_text("utf8")
        cls.acceptance=(ROOT/"scripts/office_acceptance.py").read_text("utf8")

    def test_kernel_observation_and_pinned_broker_birth_before_open(self):
        for needle in (
            "def inspect_current_root(", "observer.parse_status(",
            "observer.parse_mountinfo(", "CAP_DAC_READ_SEARCH",
            "DAC_READ_SEARCH_BIT=1<<2", "root_mount_ns_inode==host_mount_ns_inode",
            "root_net_ns_inode==host_net_ns_inode",
            "CapInh","CapPrm","CapEff","CapBnd","CapAmb",
            "seccompAllowlistAttested", "brokerProcessPinnedAttested",
            "def require_broker_lifetime(", "read_broker_start_ticks(",
            "def inspect_three_party_alignment(",
        ):
            self.assertIn(needle,self.audit)
        self.assertIn('"expectedBrokerStartTicks"',self.sender)
        self.assertIn("audit.inspect_current_root()",self.sender)
        self.assertIn("audit.require_broker_lifetime(",self.sender)
        self.assertLess(
            self.sender.index("audit.inspect_current_root()"),
            self.sender.index("handles = open_verified_handles(cfg)"))
        self.assertEqual(2,self.sender.count("audit.require_broker_lifetime("))
        self.assertLess(
            self.sender.index("sock.sendmsg("),
            self.sender.rindex("audit.require_broker_lifetime("))

    def test_no_bypass_launch_publication_or_file_mutation_in_audit(self):
        for key in (
            '"rootSandboxAttested":False',
            '"manifestTrustedByOs":False',
            '"corporateFilesAbsentAttested":False',
            '"seccompAllowlistAttested":False',
            '"mountRestrictionAttested":False',
            '"nativePublicationAuthorized":False',
            '"launchAuthorized":False',
            '"releaseReady":False',
        ):
            self.assertIn(key,self.audit)
        for forbidden in (
            "subprocess.", "os.setuid(", "os.setgid(", "os.chmod(",
            "os.chown(", "os.unlink(", "os.rename(", "os.replace(",
            "os.fork(", "os.unshare(", "os.link(", "os.mount(",
            "os.exec", "os.open(", "socket.socket(",
        ):
            self.assertNotIn(forbidden,self.audit)
        self.assertNotIn("native_saveas_root_supervisor_audit",self.broker)
        self.assertNotIn("native_saveas_root_supervisor_audit",self.worker)
        self.assertNotIn("native_saveas_root_supervisor_audit",self.cmake)
        self.assertIn("ExecStart=/usr/bin/false",self.unit)
        self.assertIn("RefuseManualStart=yes",self.unit)
        self.assertIn("GD31J27U_BLOCKED:",self.cmake)
        self.assertIn("native-saveas-root-supervisor-audit-source-tests",
                      self.acceptance)

if __name__=="__main__":
    unittest.main()
