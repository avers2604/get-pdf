"""GD31J27t DEFERRED offline tests; no actual root, service or SaveAs run.

The only filesystem fixtures are disposable synthetic files owned by the
unprivileged test user. Fake socket never transfers descriptors to a daemon.
"""
import array
import hashlib
import json
import os
from pathlib import Path
import socket
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0,str(Path(__file__).resolve().parent))
import native_saveas_root_supervisor as root
import native_saveas_fd_worker as worker

BODY=b"GETDOC_GD31J27T_SYNTHETIC_ONLY"
DIGEST=hashlib.sha256(BODY).hexdigest()


def config(worker_uid=40002):
    return {
        "schema":root.MANIFEST_SCHEMA,
        "workerUid":worker_uid,
        "brokerUid":40001,
        "brokerGid":40003,
        "expectedBrokerPid":32001,
        "fixtureName":"gd31j6_writer.docx",
        "fixtureDigests":{
            "gd31j6_writer.docx":DIGEST,
            "gd31j6_calc.xlsx":"a"*64,
            "gd31j6_impress.pptx":"b"*64,
        },
        "sessionId":"102030405060708090a0b0c0d0e0f010",
        "tabId":"f010e0d0c0b0a0908070605040302010",
        "operation":"inspect_only",
        "publishEnabled":False,
        "laboratoryOnly":True,
    }


def manifest_bytes(cfg):
    return (json.dumps(cfg,separators=(",",":"),sort_keys=True)+"\n").encode("ascii")


class FakeBroker:
    family=socket.AF_UNIX
    type=socket.SOCK_SEQPACKET
    def __init__(self,uid=40001,pid=32001,gid=40003):
        self.uid=uid;self.pid=pid;self.gid=gid
        self.sent=[];self.timeout=None
    def getsockopt(self,*args):
        return struct.pack("3i",self.pid,self.uid,self.gid)
    def settimeout(self,value):
        self.timeout=value
    def sendmsg(self, buffers, ancillary):
        self.sent.append((buffers,ancillary))
        return sum(len(item) for item in buffers)


class RootSupervisorContract(unittest.TestCase):
    def test_exact_manifest_and_bounded_inspect_only_packet(self):
        cfg=root.parse_manifest(manifest_bytes(config()))
        payload=root.build_inspect_packet(cfg)
        req=worker.parse_request(payload)
        self.assertEqual(req["sourceSha256"],DIGEST)
        self.assertEqual("inspect_only",req["operation"])
        self.assertEqual("gd31j25_writer_saved.docx",req["expectedFinalName"])

    def test_reject_unknown_duplicate_or_unsafe_manifest(self):
        wrong=[
            {**config(),"publishEnabled":True},
            {**config(),"operation":"publish"},
            {**config(),"workerUid":0},
            {**config(),"workerUid":40001},
            {**config(),"brokerUid":0},
            {**config(),"brokerGid":0},
            {**config(),"expectedBrokerPid":0},
            {**config(),"fixtureName":"../../private.docx"},
            {**config(),"fixtureDigests":{"gd31j6_writer.docx":DIGEST}},
            {**config(),"sessionId":config()["tabId"]},
            {**config(),"adminApproved":True},
        ]
        for case in wrong:
            with self.subTest(case=case),self.assertRaises(root.SupervisorError):
                root.parse_manifest(manifest_bytes(case))
        dup=manifest_bytes(config()).replace(b'"publishEnabled":false',b'"publishEnabled":false,"publishEnabled":true')
        for raw in (dup,manifest_bytes(config())[:-1],b"x"*(root.MAX_MANIFEST_BYTES+1),
                    manifest_bytes(config())+manifest_bytes(config()),
                    b'{"x":NaN}\n'):
            with self.subTest(raw=raw[:32]),self.assertRaises(root.SupervisorError):
                root.parse_manifest(raw)

    def test_wrong_peer_or_missing_operator_gate_never_opens_fds(self):
        cfg=config()
        for sock,gate in ((FakeBroker(uid=40002),root.VM_TEST_ONLY_GATE),
                          (FakeBroker(pid=32002),root.VM_TEST_ONLY_GATE),
                          (FakeBroker(gid=40004),root.VM_TEST_ONLY_GATE),
                          (FakeBroker(),None)):
            session=root.RootSendOnce()
            with self.subTest(sock=sock,gate=gate):
                with patch.object(root.os,"geteuid",return_value=0),\
                     patch.object(root.os,"getuid",return_value=0),\
                     patch.object(root,"open_verified_handles") as opener:
                    with self.assertRaises(root.SupervisorError):
                        session.send(sock,cfg,gate=gate)
                    opener.assert_not_called()
                self.assertTrue(session.consumed)
                self.assertEqual([],sock.sent)

    @unittest.skipUnless(sys.platform.startswith("linux") and os.geteuid()!=0,
                         "Only unprivileged disposable Linux fixture")
    def test_one_shot_three_preopened_fds_and_close_after_send(self):
        with tempfile.TemporaryDirectory() as temp:
            scratch=Path(temp)/"private"
            output=Path(temp)/"output"
            scratch.mkdir(mode=0o700)
            output.mkdir(mode=0o700)
            source=scratch/"gd31j6_writer.docx"
            source.write_bytes(BODY)
            (scratch/worker.PRIVATE_MARKER).write_bytes(worker.PRIVATE_BYTES)
            (output/worker.OUTPUT_MARKER).write_bytes(worker.OUTPUT_BYTES)
            flags=os.O_RDONLY|os.O_CLOEXEC
            handles=[
                os.open(source,flags),
                os.open(scratch,flags|os.O_DIRECTORY),
                os.open(output,flags|os.O_DIRECTORY),
            ]
            cfg=config(worker_uid=os.geteuid())
            sock=FakeBroker()
            session=root.RootSendOnce()
            with patch.object(root.os,"geteuid",return_value=0),\
                 patch.object(root.os,"getuid",return_value=0),\
                 patch.object(root,"open_verified_handles",return_value=handles):
                outcome=session.send(sock,cfg,gate=root.VM_TEST_ONLY_GATE)
            self.assertEqual("sent-unattested",outcome["state"])
            self.assertTrue(session.consumed)
            self.assertFalse(outcome["releaseReady"])
            self.assertFalse(outcome["nativePublicationAuthorized"])
            self.assertEqual(1,len(sock.sent))
            parts,control=sock.sent[0]
            self.assertEqual(root.build_inspect_packet(cfg),parts[0])
            self.assertEqual(socket.SCM_RIGHTS,control[0][1])
            self.assertEqual(handles,list(array.array("i",control[0][2])))
            self.assertLessEqual(sock.timeout,5)
            for fd in handles:
                with self.assertRaises(OSError):
                    os.fstat(fd)
            with self.assertRaises(root.SupervisorError):
                session.send(sock,cfg,gate=root.VM_TEST_ONLY_GATE)

    @unittest.skipUnless(sys.platform.startswith("linux") and os.geteuid()!=0,
                         "Only unprivileged Linux synthetic stub")
    def test_partial_send_is_never_replayed_and_closes_every_fd(self):
        with tempfile.TemporaryDirectory() as temp:
            handles=[]
            for i in range(3):
                p=Path(temp)/("fixture"+str(i))
                p.write_bytes(b"synthetic")
                handles.append(os.open(p,os.O_RDONLY|os.O_CLOEXEC))
            cfg=config(worker_uid=os.geteuid())
            sock=FakeBroker()
            sock.sendmsg=lambda parts,ancillary: len(parts[0])-1
            session=root.RootSendOnce()
            with patch.object(root.os,"getuid",return_value=0),\
                 patch.object(root.os,"geteuid",return_value=0),\
                 patch.object(root,"open_verified_handles",return_value=handles):
                with self.assertRaises(root.SupervisorError):
                    session.send(sock,cfg,gate=root.VM_TEST_ONLY_GATE)
            self.assertTrue(session.consumed)
            for fd in handles:
                with self.assertRaises(OSError):
                    os.fstat(fd)
            with self.assertRaises(root.SupervisorError):
                session.send(sock,cfg,gate=root.VM_TEST_ONLY_GATE)

    def test_fixed_path_policy_has_no_arbitrary_user_document_path(self):
        self.assertTrue(root.FIXED_BASE.startswith("/run/getdoc-gd31j27t-"))
        self.assertEqual("gd31j26-private-ABCDEF",root.SCRATCH)
        self.assertEqual("gd31j25-output",root.OUTPUT)
        self.assertFalse(root.denial()["writePermissionGranted"])
        self.assertFalse(root.denial()["approvedForProduction"])


if __name__=="__main__":
    unittest.main()
