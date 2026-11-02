"""GD31J27w deferred source contract: worker pidfd stays alive across IPC.

Static assertions cannot establish identity, namespace isolation or a working
Linux/Astra system policy. They must never authorize SaveAs by themselves.
"""
from pathlib import Path
import unittest

LAB=Path(__file__).resolve().parent
ROOT=LAB.parents[2]


class WorkerPidfdSourceContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.broker=(LAB/"native_saveas_fd_broker.py").read_text("utf8")
        cls.pidfd=(LAB/"native_saveas_pidfd_provenance.py").read_text("utf8")
        cls.listener=(LAB/"native_saveas_broker_listener.py").read_text("utf8")
        cls.worker=(LAB/"native_saveas_fd_worker.py").read_text("utf8")
        cls.cmake=(LAB/"CMakeLists.txt").read_text("utf8")
        cls.runner=(ROOT/"scripts/office_acceptance.py").read_text("utf8")

    def test_pidfd_required_before_kernel_preflight_and_held_across_handoff(self):
        method=self.broker.split("def handoff_after_kernel_review(",1)[1]
        for token in (
            "pidfd.pin_worker_process(", "supervisor_handoff.read_actual_handoff(",
            "pinned.assert_live()", "result = session.handoff(",
            "pinned.close()", "session.consumed = True",
            '"nativePublicationAuthorized": False',
            '"kernelIsolationAttested": False',
            '"brokerHandshakeAttested": False',
            '"launchAuthorized": False',
        ):
            with self.subTest(token=token):
                self.assertIn(token,method)
        self.assertLess(method.index("pidfd.pin_worker_process("),
                        method.index("supervisor_handoff.read_actual_handoff("))
        self.assertLess(method.index("supervisor_handoff.read_actual_handoff("),
                        method.index("result = session.handoff("))
        self.assertLess(method.index("result = session.handoff("),
                        method.rindex("pinned.assert_live()"))
        self.assertLess(method.rindex("pinned.assert_live()"),
                        method.index("pinned.close()"))
        self.assertIn("result != expected",method)
        self.assertIn("def pin_worker_process(",self.pidfd)
        self.assertIn("return pin_broker_process(pid,expected_start_ticks)",self.pidfd)

    def test_no_pidfd_getfd_no_publish_and_no_production_wiring(self):
        for forbidden in ("os.pidfd_getfd(", "pidfd_send_signal(",
                          "os.setuid(", "os.setgid(", "subprocess.",
                          "os.unlink(", "os.replace(", "os.rename(",
                          "os.chmod(", "os.chown(", "os.open("):
            self.assertNotIn(forbidden,
                             self.broker.split("def handoff_after_kernel_review(",1)[1])
        self.assertNotIn("native_saveas_pidfd_provenance",self.listener)
        self.assertNotIn("native_saveas_pidfd_provenance",self.worker)
        self.assertNotIn("native_saveas_pidfd_provenance",self.cmake)
        self.assertIn("GD31J27W_BLOCKED:",self.cmake)
        self.assertIn("native-saveas-worker-pidfd-source-tests",self.runner)


if __name__=="__main__":
    unittest.main()
