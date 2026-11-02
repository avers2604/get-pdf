"""GD31J27v static source security gates. Deferred; not a VM proof."""
from pathlib import Path
import unittest

LAB=Path(__file__).resolve().parent
ROOT=LAB.parents[2]


class PidfdSourceContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.pidfd=(LAB/"native_saveas_pidfd_provenance.py").read_text("utf8")
        cls.sender=(LAB/"native_saveas_root_supervisor.py").read_text("utf8")
        cls.broker=(LAB/"native_saveas_broker_listener.py").read_text("utf8")
        cls.worker=(LAB/"native_saveas_fd_worker.py").read_text("utf8")
        cls.cmake=(LAB/"CMakeLists.txt").read_text("utf8")
        cls.runner=(ROOT/"scripts/office_acceptance.py").read_text("utf8")

    def test_linux_pidfd_is_held_before_any_fd_open_and_after_send(self):
        for token in ("os.pidfd_open(pid,0)","select.poll()",
                      "poll.poll(0)","fcntl.FD_CLOEXEC",
                      "audit.read_broker_start_ticks(self.pid)",
                      "def pin_broker_process(", "def assert_live(self):",
                      "def close(self):","not completed",
                      "return False  # Never equate liveness"):
            with self.subTest(token=token):
                self.assertIn(token,self.pidfd)
        self.assertIn("pidfd.pin_broker_process(",self.sender)
        self.assertIn("pinned.assert_live()",self.sender)
        self.assertIn("pinned.close()",self.sender)
        self.assertLess(self.sender.index("pidfd.pin_broker_process("),
                        self.sender.index("handles = open_verified_handles(cfg)"))
        self.assertLess(self.sender.index("handles = open_verified_handles(cfg)"),
                        self.sender.index("sock.sendmsg("))
        self.assertLess(self.sender.index("sock.sendmsg("),
                        self.sender.rindex("pinned.assert_live()"))
        self.assertLess(self.sender.index("sock.sendmsg("),
                        self.sender.index("pinned.close()"))

    def test_readonly_provenance_no_signals_ptrace_or_pidfd_getfd(self):
        for forbidden in ("subprocess.", "os.pidfd_getfd(", "pidfd_send_signal(",
                          "os.kill(", "os.waitpid(", "os.setuid(",
                          "os.setgid(", "os.chmod(", "os.unlink(",
                          "os.rename(", "os.link(", "os.open(",
                          "os.fork(", "os.exec", "os.unshare(",
                          "os.setns(", "socket.socket("):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden,self.pidfd)
        self.assertNotIn("native_saveas_pidfd_provenance",self.worker)
        self.assertNotIn("native_saveas_pidfd_provenance",self.broker)
        self.assertNotIn("native_saveas_pidfd_provenance",self.cmake)
        self.assertIn("processIdentityAttested=False",self.pidfd)
        self.assertIn("launchAuthorized=False",self.pidfd)
        self.assertIn("releaseReady=False",self.pidfd)
        self.assertIn("GD31J27V_BLOCKED:",self.cmake)
        self.assertIn("native-saveas-broker-pidfd-source-tests",self.runner)


if __name__=="__main__":
    unittest.main()
