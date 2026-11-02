"""GD31J27x: deferred socket-bound SO_PEERPIDFD tests, all mocked.

Does NOT inspect real processes, call live getsockopt or transfer actual FDs.
For disposable offline Astra Linux VM only; source contract is not attestation.
"""
import errno
import fcntl
import os
from pathlib import Path
import socket
import struct
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_saveas_socket_peerpidfd as peer
import native_saveas_pidfd_provenance as pidfd

class FakeSocket:
    family=socket.AF_UNIX
    type=socket.SOCK_SEQPACKET
    def __init__(self, *, pid=33001, uid=40002, gid=40003,
                 peer_fd=71, broken=None, malformed=False):
        self.pid=pid
        self.uid=uid
        self.gid=gid
        self.peer_fd=peer_fd
        self.broken=broken
        self.malformed=malformed
        self.options=[]
    def getsockopt(self, level, option, size):
        self.options.append((level,option,size))
        if option==socket.SO_PEERCRED:
            return struct.pack("3i",self.pid,self.uid,self.gid)
        if option==peer.LINUX_SO_PEERPIDFD:
            if self.broken is not None:
                raise OSError(self.broken,"mock unsupported peer-pidfd")
            return b"\x01" if self.malformed else struct.pack("i",self.peer_fd)
        raise AssertionError("unexpected kernel option")

class SocketPeerPidfdTests(unittest.TestCase):
    def test_kernel_peer_pidfd_is_referenced_and_closed_even_on_positive_denial(self):
        sock=FakeSocket()
        with mock.patch.object(peer.platform,"machine",return_value="x86_64"),\
             mock.patch.object(peer.fcntl,"fcntl",return_value=fcntl.FD_CLOEXEC),\
             mock.patch.object(peer.os,"close") as close,\
             mock.patch.object(peer.audit,"read_broker_start_ticks",
                               return_value=123456) as ticks,\
             mock.patch.object(peer.pidfd,"_poll_still_running") as liveness:
            with peer.pin_socket_peer(sock,expected_worker_pid=33001,
                                      expected_worker_uid=40002,
                                      broker_uid=40001,
                                      expected_worker_start_ticks=123456) as pinned:
                self.assertEqual(71,pinned.fileno())
                self.assertIs(pinned.assert_live(),False)
                self.assertIs(pinned.socketPeerAttested,False)
                self.assertIs(pinned.nativePublicationAuthorized,False)
                self.assertIs(pinned.releaseReady,False)
            close.assert_called_once_with(71)
            self.assertGreaterEqual(ticks.call_count,3)
            self.assertGreaterEqual(liveness.call_count,2)
        self.assertIn((socket.SOL_SOCKET,peer.LINUX_SO_PEERPIDFD,4),sock.options)

    def test_unknown_kernel_or_arch_is_hard_refusal_without_numeric_pid_fallback(self):
        for arch in ("mips","sparc","armv7l"):
            sock=FakeSocket()
            with mock.patch.object(peer.platform,"machine",return_value=arch):
                with self.subTest(arch=arch),self.assertRaises(peer.PeerPidfdError):
                    peer.pin_socket_peer(sock,expected_worker_pid=33001,
                                         expected_worker_uid=40002,
                                         broker_uid=40001,
                                         expected_worker_start_ticks=123456)
            self.assertEqual([],sock.options)
        for failure in (errno.ENOPROTOOPT,errno.EOPNOTSUPP,errno.EINVAL):
            sock=FakeSocket(broken=failure)
            with mock.patch.object(peer.platform,"machine",return_value="x86_64"),\
                 mock.patch.object(peer.os,"close") as close:
                with self.subTest(failure=failure),self.assertRaises(peer.PeerPidfdError):
                    peer.pin_socket_peer(sock,expected_worker_pid=33001,
                                         expected_worker_uid=40002,
                                         broker_uid=40001,
                                         expected_worker_start_ticks=123456)
                close.assert_not_called()

    def test_peer_cred_mismatch_refused_before_so_peerpidfd_request(self):
        for pid,uid,gid in ((33002,40002,40003),(33001,40001,40003),
                            (33001,0,40003),(33001,40002,0)):
            sock=FakeSocket(pid=pid,uid=uid,gid=gid)
            with mock.patch.object(peer.platform,"machine",return_value="x86_64"):
                with self.subTest(pid=pid,uid=uid,gid=gid),\
                     self.assertRaises(peer.PeerPidfdError):
                    peer.pin_socket_peer(sock,expected_worker_pid=33001,
                                         expected_worker_uid=40002,
                                         broker_uid=40001,
                                         expected_worker_start_ticks=123456)
            self.assertEqual(1,len(sock.options))

    def test_malformed_return_or_missing_cloexec_closes_or_refuses(self):
        sock=FakeSocket(malformed=True)
        with mock.patch.object(peer.platform,"machine",return_value="x86_64"),\
             mock.patch.object(peer.os,"close") as close:
            with self.assertRaises(peer.PeerPidfdError):
                peer.pin_socket_peer(sock,expected_worker_pid=33001,
                                     expected_worker_uid=40002,
                                     broker_uid=40001,
                                     expected_worker_start_ticks=123456)
            close.assert_not_called()
        sock=FakeSocket()
        with mock.patch.object(peer.platform,"machine",return_value="x86_64"),\
             mock.patch.object(peer.fcntl,"fcntl",return_value=0),\
             mock.patch.object(peer.os,"close") as close:
            with self.assertRaises(peer.PeerPidfdError):
                peer.pin_socket_peer(sock,expected_worker_pid=33001,
                                     expected_worker_uid=40002,
                                     broker_uid=40001,
                                     expected_worker_start_ticks=123456)
            close.assert_called_once_with(71)

    def test_exited_socket_peer_or_recycled_pid_refused_and_fd_closed(self):
        for ticks in ((123457,),(123456,123457)):
            sock=FakeSocket()
            with mock.patch.object(peer.platform,"machine",return_value="x86_64"),\
                 mock.patch.object(peer.fcntl,"fcntl",return_value=fcntl.FD_CLOEXEC),\
                 mock.patch.object(peer.audit,"read_broker_start_ticks",
                                   side_effect=ticks),\
                 mock.patch.object(peer.os,"close") as close,\
                 mock.patch.object(peer.pidfd,"_poll_still_running"):
                with self.subTest(ticks=ticks),self.assertRaises(peer.PeerPidfdError):
                    peer.pin_socket_peer(sock,expected_worker_pid=33001,
                                         expected_worker_uid=40002,
                                         broker_uid=40001,
                                         expected_worker_start_ticks=123456)
                close.assert_called_once_with(71)
        sock=FakeSocket()
        with mock.patch.object(peer.platform,"machine",return_value="x86_64"),\
             mock.patch.object(peer.fcntl,"fcntl",return_value=fcntl.FD_CLOEXEC),\
             mock.patch.object(peer.audit,"read_broker_start_ticks",
                               return_value=123456),\
             mock.patch.object(peer.os,"close") as close,\
             mock.patch.object(peer.pidfd,"_poll_still_running",
                               side_effect=pidfd.PidfdError("mock exit")):
            with self.assertRaises(peer.PeerPidfdError):
                peer.pin_socket_peer(sock,expected_worker_pid=33001,
                                     expected_worker_uid=40002,
                                     broker_uid=40001,
                                     expected_worker_start_ticks=123456)
            close.assert_called_once_with(71)

    def test_closed_stdio_refused_before_kernel_allocates_a_new_descriptor(self):
        sock=FakeSocket()
        with mock.patch.object(peer.platform,"machine",return_value="x86_64"),\
             mock.patch.object(peer.os,"fstat",side_effect=OSError(9,"closed stdio")),\
             mock.patch.object(peer.os,"close") as close:
            with self.assertRaises(peer.PeerPidfdError):
                peer.pin_socket_peer(sock,expected_worker_pid=33001,
                                     expected_worker_uid=40002,
                                     broker_uid=40001,
                                     expected_worker_start_ticks=123456)
            self.assertEqual(1,len(sock.options))
            close.assert_not_called()

    def test_bool_or_missing_pin_is_refused(self):
        for args in (
            {"expected_worker_pid":True},
            {"expected_worker_uid":0},
            {"broker_uid":0},
            {"expected_worker_start_ticks":True},
            {"expected_worker_start_ticks":-1},
        ):
            values={"expected_worker_pid":33001,"expected_worker_uid":40002,
                    "broker_uid":40001,"expected_worker_start_ticks":123456}
            values.update(args)
            with mock.patch.object(peer.platform,"machine",return_value="x86_64"):
                with self.subTest(args=args),self.assertRaises(peer.PeerPidfdError):
                    peer.pin_socket_peer(FakeSocket(),**values)

if __name__=="__main__":
    unittest.main()
