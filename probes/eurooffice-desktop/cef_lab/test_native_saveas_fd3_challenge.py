"""GD31J27y: DEFERRED mock-only FD3 challenge tests, no system effects.

Does not establish executable provenance, kernel socket pairing proof,
Astra MAC, sandbox validity or permission to save user documents.
"""
from pathlib import Path
import os
import socket
import stat
import struct
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_saveas_fd3_challenge as proof

NONCE=b"n"*32
INODE=1234567

class SocketMock:
    family=socket.AF_UNIX
    type=socket.SOCK_SEQPACKET
    def __init__(self, *, fd=3, peer_pid=22000, peer_uid=40001,
                 peer_gid=40003, input_packet=b"", flags=0, anc=()):
        self.fd=fd;self.pid=peer_pid;self.uid=peer_uid;self.gid=peer_gid
        self.packet=input_packet;self.flags=flags;self.anc=anc
        self.sent=[];self.reads=0;self.timeouts=[]
    def fileno(self):
        return self.fd
    def getsockopt(self,level,kind,length):
        if kind==socket.SO_PEERCRED:
            return struct.pack("3i",self.pid,self.uid,self.gid)
        raise AssertionError("unsupported kernel request")
    def settimeout(self,value):
        self.timeouts.append(value)
    def recvmsg(self,size,ancsize,flags):
        self.reads+=1
        return self.packet,self.anc,self.flags,None
    def sendall(self,packet):
        self.sent.append(packet)

class Fd3ChallengeTest(unittest.TestCase):
    def setUp(self):
        p=patch.object(proof.os,"geteuid",return_value=40001)
        p.start()
        self.addCleanup(p.stop)

    def test_worker_receives_challenge_only_on_exact_fd3_then_echoes_inode(self):
        received=proof.CHALLENGE_PREFIX+NONCE
        channel=SocketMock(input_packet=received)
        fake=type("Meta",(),{"st_ino":INODE,"st_dev":9,"st_mode":stat.S_IFSOCK})()
        with patch.object(proof.os,"fstat",return_value=fake),\
             patch.object(proof.os,"geteuid",return_value=40002):
            report=proof.respond_fd3_once(channel,expected_broker_pid=22000,
                                          expected_broker_uid=40001)
        self.assertEqual(proof.ACK_PREFIX+NONCE+struct.pack("!Q",INODE),
                         channel.sent[0])
        self.assertEqual(1,channel.reads)
        self.assertIs(report["fd3PairedAttested"],False)
        self.assertIs(report["nativePublicationAuthorized"],False)
        self.assertIs(report["releaseReady"],False)

    def test_worker_rejects_wrong_slot_wrong_uid_and_unexpected_ancillary(self):
        msg=proof.CHALLENGE_PREFIX+NONCE
        cases=[
            SocketMock(fd=4,input_packet=msg),
            SocketMock(peer_uid=40002,input_packet=msg),
            SocketMock(input_packet=msg,flags=socket.MSG_CTRUNC),
            SocketMock(input_packet=msg,flags=socket.MSG_TRUNC),
            SocketMock(input_packet=msg,anc=[(socket.SOL_SOCKET,socket.SCM_RIGHTS,b"x")]),
            SocketMock(input_packet=msg+b"extra"),
            SocketMock(input_packet=proof.CHALLENGE_PREFIX+b"\x00"*32),
        ]
        fake=type("Meta",(),{"st_ino":INODE,"st_dev":9,"st_mode":stat.S_IFSOCK})()
        with patch.object(proof.os,"fstat",return_value=fake),\
             patch.object(proof.os,"geteuid",return_value=40002):
            for channel in cases:
                with self.subTest(channel=channel),self.assertRaises(proof.Fd3ChallengeError):
                    proof.respond_fd3_once(channel,expected_broker_pid=22000,
                                          expected_broker_uid=40001)
                self.assertEqual([],channel.sent)

    def test_broker_nonce_and_kernel_inode_binding_only_diagnostic(self):
        reply=proof.ACK_PREFIX+NONCE+struct.pack("!Q",INODE)
        channel=SocketMock(fd=9,peer_pid=33000,peer_uid=40002,
                           input_packet=reply)
        with patch.object(proof.os,"urandom",return_value=NONCE),\
             patch.object(proof.os,"readlink",return_value=f"socket:[{INODE}]") as read:
            report=proof.challenge_worker_fd3_once(
                channel,expected_worker_pid=33000,expected_worker_uid=40002,
                broker_uid=40001)
        self.assertEqual(2,read.call_count)
        self.assertEqual([proof.CHALLENGE_PREFIX+NONCE],channel.sent)
        self.assertIs(report["workerFd3HandshakeObserved"],True)
        self.assertIs(report["fd3PairedAttested"],False)
        self.assertIs(report["workerExecutableAttested"],False)
        self.assertIs(report["nativePublicationAuthorized"],False)
        self.assertIs(report["releaseReady"],False)

    def test_broker_rejects_wrong_inode_nonce_uid_and_truncation(self):
        correct=proof.ACK_PREFIX+NONCE+struct.pack("!Q",INODE)
        cases=[
            (SocketMock(fd=9,peer_pid=33000,peer_uid=40002,
                        input_packet=correct[:-1]+b"x"), f"socket:[{INODE}]"),
            (SocketMock(fd=9,peer_pid=33000,peer_uid=40002,
                        input_packet=proof.ACK_PREFIX+b"x"*32+struct.pack("!Q",INODE)),
             f"socket:[{INODE}]"),
            (SocketMock(fd=9,peer_pid=33000,peer_uid=40001,
                        input_packet=correct), f"socket:[{INODE}]"),
            (SocketMock(fd=9,peer_pid=33000,peer_uid=40002,
                        input_packet=correct, flags=socket.MSG_CTRUNC),
             f"socket:[{INODE}]"),
            (SocketMock(fd=9,peer_pid=33000,peer_uid=40002,
                        input_packet=correct), f"socket:[{INODE+1}]"),
        ]
        for channel,target in cases:
            with self.subTest(target=target),\
                 patch.object(proof.os,"urandom",return_value=NONCE),\
                 patch.object(proof.os,"readlink",return_value=target):
                with self.assertRaises(proof.Fd3ChallengeError):
                    proof.challenge_worker_fd3_once(
                        channel,expected_worker_pid=33000,
                        expected_worker_uid=40002,broker_uid=40001)
                self.assertLessEqual(channel.reads,1)

    def test_no_duplicate_challenge_and_bad_nonce_entropy_fails_before_send(self):
        sock=SocketMock(fd=9,peer_pid=33000,peer_uid=40002)
        with patch.object(proof.os,"urandom",return_value=b"short"),\
             patch.object(proof.os,"readlink",return_value=f"socket:[{INODE}]"):
            with self.assertRaises(proof.Fd3ChallengeError):
                proof.challenge_worker_fd3_once(
                    sock,expected_worker_pid=33000,
                    expected_worker_uid=40002,broker_uid=40001)
        self.assertEqual([],sock.sent)

if __name__=="__main__":
    unittest.main()
