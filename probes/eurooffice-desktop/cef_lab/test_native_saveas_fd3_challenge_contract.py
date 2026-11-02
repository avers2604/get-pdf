"""GD31J27y static deny-only source contract, not a live VM attestation."""
from pathlib import Path
import unittest

LAB=Path(__file__).resolve().parent
ROOT=LAB.parents[2]

class Fd3ChallengeSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.challenge=(LAB/"native_saveas_fd3_challenge.py").read_text("utf8")
        cls.flow=(LAB/"native_saveas_fd3_challenge_flow.py").read_text("utf8")
        cls.worker=(LAB/"native_saveas_fd_worker.py").read_text("utf8")
        cls.listener=(LAB/"native_saveas_broker_listener.py").read_text("utf8")
        cls.dialer=(LAB/"native_saveas_worker_dialer.py").read_text("utf8")
        cls.cmake=(LAB/"CMakeLists.txt").read_text("utf8")
        cls.acceptance=(ROOT/"scripts/office_acceptance.py").read_text("utf8")

    def test_exact_fd3_nonce_no_scm_rights_challenge(self):
        for needle in (
            'NONCE_BYTES=32','_fd3_inode(pid)', 'os.readlink(f"/proc/{pid}/fd/3")',
            'sock.fileno()!=3','os.fstat(3)',
            'socket.SO_PEERCRED','os.urandom(NONCE_BYTES)',
            'sock.recvmsg(', 'size+1,0,socket.MSG_CMSG_CLOEXEC',
            'socket.MSG_TRUNC|socket.MSG_CTRUNC',
            'CHALLENGE_PREFIX','ACK_PREFIX','stat.S_ISSOCK(',
            'fd3PairedAttested":False','workerExecutableAttested":False',
            'nativePublicationAuthorized":False','releaseReady":False',
        ):
            with self.subTest(needle=needle):
                self.assertIn(needle,self.challenge)

    def test_explicit_only_worker_and_broker_routes_remain_denied(self):
        self.assertIn('def lab_broker_fd3_then_denied(',self.flow)
        self.assertIn('def lab_worker_fd3_then_denied(',self.flow)
        self.assertIn('peer.pin_socket_peer(',self.flow)
        self.assertIn('proof.challenge_worker_fd3_once(',self.flow)
        self.assertIn('broker.handoff_after_kernel_review(',self.flow)
        self.assertIn('worker.serve_one(sock)',self.flow)
        self.assertIn('session.consumed=True',self.flow)
        self.assertNotIn('native_saveas_fd3_challenge',self.worker)
        self.assertNotIn('native_saveas_fd3_challenge',self.listener)
        self.assertNotIn('native_saveas_fd3_challenge',self.dialer)
        self.assertNotIn('native_saveas_fd3_challenge',self.cmake)

    def test_no_privilege_changes_or_file_publication(self):
        for source in (self.challenge,self.flow):
            for bad in (
                'subprocess.', 'os.setuid(', 'os.setgid(', 'os.chown(',
                'os.chmod(', 'os.unlink(', 'os.rename(', 'os.replace(',
                'os.link(', 'os.fork(', 'os.exec', 'os.open(',
                'pidfd_getfd(', 'socket.socket(', 'os.kill(',
                'publish_disposable_fixture(',
            ):
                with self.subTest(bad=bad):
                    self.assertNotIn(bad,source)
        self.assertIn("GD31J27Y_BLOCKED:",self.cmake)
        self.assertIn("native-saveas-fd3-challenge-contract-tests",self.acceptance)

if __name__=="__main__":
    unittest.main()
