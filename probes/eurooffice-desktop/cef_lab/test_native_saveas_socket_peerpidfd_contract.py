"""GD31J27x static-only source contracts, not a verified Astra sandbox."""
from pathlib import Path
import unittest

LAB=Path(__file__).resolve().parent
ROOT=LAB.parents[2]

class SocketBoundPeerSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.peer=(LAB/"native_saveas_socket_peerpidfd.py").read_text("utf8")
        cls.broker=(LAB/"native_saveas_fd_broker.py").read_text("utf8")
        cls.worker=(LAB/"native_saveas_fd_worker.py").read_text("utf8")
        cls.listener=(LAB/"native_saveas_broker_listener.py").read_text("utf8")
        cls.cmake=(LAB/"CMakeLists.txt").read_text("utf8")
        cls.runner=(ROOT/"scripts/office_acceptance.py").read_text("utf8")

    def test_kernel_6_5_socket_peer_pidfd_required_and_closed(self):
        for item in (
            "LINUX_SO_PEERPIDFD = 77",
            'sock.getsockopt(socket.SOL_SOCKET,',
            "socket.SO_PEERCRED",
            "LINUX_SO_PEERPIDFD, _FD_SIZE",
            "fcntl.FD_CLOEXEC",
            "pidfd._poll_still_running(fd)",
            "audit.read_broker_start_ticks(self.pid)",
            "def pin_socket_peer(",
            "received_fd = struct.unpack",
            "os.close(received_fd)",
            "socketPeerAttested = False",
            "nativePublicationAuthorized = False",
            "releaseReady = False",
        ):
            with self.subTest(item=item):
                self.assertIn(item,self.peer)
        source=self.broker.split("def handoff_after_kernel_review(",1)[1]
        self.assertIn("socket_peer.pin_socket_peer(",source)
        self.assertIn("peer_reference.assert_live()",source)
        self.assertIn("peer_reference.close()",source)
        self.assertIn("pinned.close()",source)
        self.assertLess(source.index("socket_peer.pin_socket_peer("),
                        source.index("supervisor_handoff.read_actual_handoff("))
        self.assertLess(source.index("supervisor_handoff.read_actual_handoff("),
                        source.index("result = session.handoff("))
        self.assertLess(source.index("result = session.handoff("),
                        source.rindex("peer_reference.assert_live()"))
        self.assertLess(source.rindex("peer_reference.assert_live()"),
                        source.index("peer_reference.close()"))

    def test_no_fallback_to_numeric_pid_or_enabling_file_publication(self):
        for term in (
            "os.pidfd_getfd(", "pidfd_send_signal(", "os.kill(",
            "subprocess.", "os.setuid(", "os.setgid(", "os.chmod(",
            "os.chown(", "os.unlink(", "os.replace(", "os.rename(",
            "os.open(", "os.fork(", "os.setns(", "os.unshare(",
        ):
            with self.subTest(term=term):
                self.assertNotIn(term,self.peer)
        self.assertNotIn("native_saveas_socket_peerpidfd",self.worker)
        self.assertNotIn("native_saveas_socket_peerpidfd",self.listener)
        self.assertNotIn("native_saveas_socket_peerpidfd",self.cmake)
        self.assertIn("GD31J27X_BLOCKED:",self.cmake)
        self.assertIn("native-saveas-socket-peerpidfd-source-tests",self.runner)


if __name__=="__main__":
    unittest.main()
