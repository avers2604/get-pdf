"""GD31J27ah deferred synthetic mock tests; nothing launches or reads real docs.

Tests deliberately never call role main with valid setup. No VM, root,
socket, subprocess, service, system test or build is executed by this PR.
"""
import array
import os
import socket
import stat
import struct
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch

sys.path.insert(0,str(Path(__file__).resolve().parent))
import native_saveas_manual_triparty_devnull as lab


class MockSocket:
    family=socket.AF_UNIX
    type=socket.SOCK_SEQPACKET
    def __init__(self,peer_uid=44001,peer_gid=44003):
        self.peer_uid=peer_uid
        self.peer_gid=peer_gid
        self.sent=[]
    def getsockopt(self,level,option,length):
        assert option==socket.SO_PEERCRED
        return struct.pack("3i",33221,self.peer_uid,self.peer_gid)
    def sendmsg(self,parts,control):
        self.sent.append((parts,control))
        return sum(len(x) for x in parts)
    def sendall(self,payload):
        self.sent.append(payload)
    def recv(self,limit):
        return lab.DENIED


class TripartyManualVMTests(unittest.TestCase):
    def test_reject_all_unapproved_roles_or_implicit_exec(self):
        for args in (
            [],["broker"],["worker","ack"],
            ["supervisor","--force"],["unknown",lab.MANUAL_ACK],
            ["worker",lab.MANUAL_ACK,"--publish"],
        ):
            with self.subTest(args=args),patch.object(lab,"_preflight") as p:
                self.assertEqual(2,lab.main(args))
                p.assert_not_called()

    def test_identity_is_fixed_and_fails_closed(self):
        with patch.object(lab.sys,"platform","linux"),\
             patch.object(lab.os,"getuid",return_value=44001),\
             patch.object(lab.os,"geteuid",return_value=44001),\
             patch.object(lab.os,"getgid",return_value=44003),\
             patch.object(lab.os,"getegid",return_value=44003),\
             patch.dict(lab.os.environ,{lab.VM_ENV:lab.VM_ENV_VALUE}):
            lab._preflight("broker")
            with self.assertRaises(lab.LabError):
                lab._preflight("worker")
            with self.assertRaises(lab.LabError):
                lab._preflight("supervisor")
        with patch.object(lab.sys,"platform","linux"),\
             patch.object(lab.os,"getuid",return_value=44001),\
             patch.object(lab.os,"geteuid",return_value=0),\
             patch.object(lab.os,"getgid",return_value=44003),\
             patch.object(lab.os,"getegid",return_value=44003),\
             patch.dict(lab.os.environ,{lab.VM_ENV:lab.VM_ENV_VALUE}):
            with self.assertRaises(lab.LabError):
                lab._preflight("broker")

    def test_so_peercred_requires_exact_distinct_role_and_group(self):
        lab._peer_check(MockSocket(44001,44003),44001)
        for uid,gid in ((0,44003),(44002,44003),(44001,40000)):
            with self.subTest(uid=uid,gid=gid),self.assertRaises(lab.LabError):
                lab._peer_check(MockSocket(uid,gid),44001)

    def test_three_synthetic_fds_only_and_no_write_modes(self):
        params=(array.array("i",[8,9,10]).tobytes())
        controls=[(socket.SOL_SOCKET,socket.SCM_RIGHTS,params)]
        mode=stat.S_IFCHR|0o666
        fake=SimpleNamespace(st_mode=mode,st_rdev=259)
        with patch.object(lab.os,"fstat",return_value=fake),\
             patch.object(lab.os,"stat",return_value=fake),\
             patch.object(lab.fcntl,"fcntl",return_value=os.O_RDONLY):
            self.assertEqual((8,9,10),lab._check_fd_roles([8,9,10]))
            self.assertEqual((8,9,10),lab._extract_exact_rights(controls,0))
        for xs in ([8,9],[8,9,9],[0,9,10],[-1,9,10]):
            with self.subTest(xs=xs),self.assertRaises(lab.LabError):
                lab._check_fd_roles(xs)
        with self.assertRaises(lab.LabError):
            lab._extract_exact_rights(controls,socket.MSG_CTRUNC)
        with self.assertRaises(lab.LabError):
            lab._extract_exact_rights(controls+controls,0)
        with patch.object(lab.os,"fstat",return_value=fake),\
             patch.object(lab.os,"stat",return_value=fake),\
             patch.object(lab.fcntl,"fcntl",return_value=os.O_WRONLY):
            with self.assertRaises(lab.LabError):
                lab._check_fd_roles([8,9,10])

    def test_invalid_scm_rights_packet_closes_every_received_fd(self):
        three=array.array("i",[8,9,10]).tobytes()
        packet=[(socket.SOL_SOCKET,socket.SCM_RIGHTS,three)]
        sock=MockSocket()
        sock.recvmsg=lambda size,ancsize:(b"bad-packet",packet,0,None)
        with patch.object(lab.os,"close") as close:
            with self.assertRaises(lab.LabError):
                lab._recv_three(sock)
            self.assertEqual({8,9,10},{x.args[0] for x in close.call_args_list})
            self.assertEqual(3,close.call_count)

    def test_short_sendmsg_is_denied_without_retry(self):
        sock=MockSocket()
        sock.sendmsg=lambda parts,control:len(lab.REQUEST)-1
        with patch.object(lab,"_check_fd_roles",return_value=(8,9,10)):
            with self.assertRaises(lab.LabError):
                lab._send_exact_three(sock,[8,9,10])

    def test_supervisor_sends_only_fixed_deny_packet(self):
        sock=MockSocket(peer_uid=44001)
        with patch.object(lab,"_check_fd_roles",return_value=(8,9,10)):
            lab._send_exact_three(sock,[8,9,10])
        self.assertEqual(1,len(sock.sent))
        payloads,controls=sock.sent[0]
        self.assertEqual([lab.REQUEST],payloads)
        self.assertEqual(socket.SCM_RIGHTS,controls[0][1])

    def test_transcript_outcome_is_always_denied(self):
        self.assertEqual("denied",lab.DENIAL_STATE)
        self.assertIn(b'"nativePublicationAuthorized":false',lab.DENIED)
        self.assertIn(b'"releaseReady":false',lab.DENIED)
        for payload in (
            b'{"state":"saved"}\n',
            b"ok",
            lab.DENIED+b"x",
        ):
            with self.subTest(payload=payload),self.assertRaises(lab.LabError):
                lab._require_denial(payload)
        self.assertFalse(lab.denial()["releaseReady"])
        self.assertFalse(lab.denial()["nativePublicationAuthorized"])

    def test_socket_parent_is_preprovisioned_and_never_unlinked(self):
        expected={
            lab.BROKER_DIR:(44001,44003,0o710),
            lab.WORKER_DIR:(44002,44003,0o710),
        }
        def fake_stat(path):
            if path==lab.BASE:
                return SimpleNamespace(st_uid=0,st_gid=0,
                                       st_mode=stat.S_IFDIR|0o755)
            uid,gid,mode=expected[path]
            return SimpleNamespace(st_uid=uid,st_gid=gid,
                                   st_mode=stat.S_IFDIR|mode)
        with patch.object(lab.os,"lstat",side_effect=fake_stat):
            lab._check_parent("broker")
            lab._check_parent("worker")
        with patch.object(lab.os,"lstat",side_effect=OSError(2,"absent")):
            with self.assertRaises(lab.LabError):
                lab._check_parent("broker")

if __name__=="__main__":
    unittest.main()
