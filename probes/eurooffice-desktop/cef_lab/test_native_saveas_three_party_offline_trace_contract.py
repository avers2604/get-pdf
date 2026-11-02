"""GD31J27ag source contracts for pure, unwired simulated triparty traces."""
from pathlib import Path
import unittest
LAB=Path(__file__).resolve().parent
ROOT=LAB.parents[2]

class TraceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.src=(LAB/"native_saveas_three_party_offline_trace.py").read_text("utf8")
        cls.worker=(LAB/"native_saveas_fd_worker.py").read_text("utf8")
        cls.broker=(LAB/"native_saveas_fd_broker.py").read_text("utf8")
        cls.listener=(LAB/"native_saveas_broker_listener.py").read_text("utf8")
        cls.root=(LAB/"native_saveas_root_supervisor.py").read_text("utf8")
        cls.cmake=(LAB/"CMakeLists.txt").read_text("utf8")
        cls.acceptance=(ROOT/"scripts/office_acceptance.py").read_text("utf8")

    def test_simulated_order_one_time_and_all_authority_flags_false(self):
        for required in (
            'EXPECTED_EVENTS=(', '"supervisor","vm-preflight","observed"',
            '"supervisor","synthetic-fds-offered","observed"',
            '"broker","nonce-issued","observed"',
            '"worker","inspect-only-refusal","denied"',
            '"broker","refusal-received","denied"',
            '"supervisor","no-write-cleanup-observed","observed"',
            'class OneShotTraceReview:', 'self.consumed=True',
            'object_pairs_hook=_unique_pairs',
            'hmac.compare_digest(',
            '"nativePublicationAuthorized":False',
            '"transportAttested":False',
            '"trustedVmIsolationAttested":False',
            '"releaseReady":False',
        ):
            with self.subTest(required=required):
                self.assertIn(required,self.src)

    def test_no_runtime_wiring_io_or_privileges(self):
        for src in (self.worker,self.broker,self.listener,self.root,self.cmake):
            self.assertNotIn("native_saveas_three_party_offline_trace",src)
        for forbidden in (
            "subprocess.", "os.open(", "os.close(", "os.read(", "os.write(",
            "open(", "os.setuid(", "os.setgid(", "os.fork(",
            "os.exec", "os.rename(", "os.unlink(", "os.replace(",
            "socket.socket(", "socketpair(", "os.pidfd_open(",
            "os.kill(", "systemctl ", "popen(", "threading.",
            "os.chmod(", "os.chown(",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden,self.src)
        self.assertIn("GD31J27AG_BLOCKED:",self.cmake)
        self.assertIn("native-saveas-three-party-trace-source-tests",
                      self.acceptance)

if __name__=="__main__":
    unittest.main()
