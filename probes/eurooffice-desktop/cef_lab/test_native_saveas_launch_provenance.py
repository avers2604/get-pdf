"""GD31J27z DEFERRED tests for strict root-supplied launcher pins.

No Python tests should be executed until offline Astra VM acceptance.
All code in this suite works with synthetic facts/mock fd handles only.
"""
import hashlib
import json
import os
from pathlib import Path
import stat
import sys
import unittest
from unittest.mock import patch, MagicMock

sys.path.insert(0,str(Path(__file__).resolve().parent))
import native_saveas_launch_provenance as provenance

EXE=b"pinned CPython interpreter synthetic bytes"
SRC=b"fixed worker Python source for offline test only"
EXE_SHA=hashlib.sha256(EXE).hexdigest()
SRC_SHA=hashlib.sha256(SRC).hexdigest()

def manifest():
    def p(role,pid,uid,gid,ticks):
        path=provenance.SCRIPT_PATHS[role]
        return {
            "pid":pid,"uid":uid,"gid":gid,"startTicks":ticks,
            "cgroup":provenance.CGROUP_PATHS[role],
            "exeTarget":"/usr/bin/python3.11","exeSha256":EXE_SHA,
            "scriptSha256":SRC_SHA,
            "argv":["/usr/bin/python3","-I","-S",path],
        }
    return {
        "schema":provenance.MANIFEST_SCHEMA,"laboratoryOnly":True,
        "publishEnabled":False,"mode":"inspect_only",
        "profiles":{
            "broker":p("broker",32001,40001,40003,123456),
            "worker":p("worker",33001,40002,40003,123457),
        },
    }

class ManifestTests(unittest.TestCase):
    def test_exact_pins_and_fixed_non_user_paths_only(self):
        raw=(json.dumps(manifest(),separators=(",",":"),sort_keys=True)+"\n").encode()
        obj=provenance.parse_pinned_manifest(raw)
        self.assertEqual("inspect_only",obj["mode"])
        self.assertEqual(40002,obj["profiles"]["worker"]["uid"])
        self.assertFalse(provenance.denial()["releaseReady"])

    def test_unknown_names_duplicate_keys_and_unsafe_pins_refused(self):
        source=manifest()
        bad=[
            {**source,"publishEnabled":True},
            {**source,"mode":"publish"},
            {**source,"extra":False},
            {**source,"profiles":{"worker":source["profiles"]["worker"]}},
        ]
        for item in bad:
            with self.subTest(item=str(item)[:20]),self.assertRaises(provenance.ProvenanceError):
                provenance.parse_pinned_manifest((json.dumps(item)+"\n").encode())
        for role,key,val in (
            ("worker","uid",0),("worker","uid",40001),
            ("broker","pid",33001),("worker","startTicks",True),
            ("worker","cgroup","/system.slice/other.service"),
            ("worker","exeSha256","0"*64),
            ("broker","argv",["python","-c","exit()"]),
            ("worker","exeTarget","/tmp/evil"),
            ("worker","scriptSha256","aaaa"),
        ):
            item=manifest()
            item["profiles"][role][key]=val
            with self.subTest(role=role,key=key),self.assertRaises(provenance.ProvenanceError):
                provenance.parse_pinned_manifest((json.dumps(item)+"\n").encode())
        valid=(json.dumps(manifest(),separators=(",",":"))+"\n").encode()
        duplicate=valid.replace(b'"publishEnabled": false',b'"publishEnabled": false, "publishEnabled": true')
        # Compact serialization, duplicate an existing key explicitly.
        dup=valid.replace(b'"publishEnabled":false',b'"publishEnabled":false,"publishEnabled":true')
        for raw in (dup,valid.rstrip(b"\n"),valid+valid,
                    b"x"*(provenance.MAX_MANIFEST+1),b'{"x":NaN}\n'):
            with self.subTest(raw=raw[:30]),self.assertRaises(provenance.ProvenanceError):
                provenance.parse_pinned_manifest(raw)

class SnapshotTests(unittest.TestCase):
    def test_kernel_snapshot_compares_cgroup_argv_and_all_four_ids(self):
        profile=manifest()["profiles"]["worker"]
        snapshot={
            "cgroup":"0::"+profile["cgroup"]+"\n",
            "cmdline":b"\x00".join(x.encode() for x in profile["argv"])+b"\x00",
            "status":"Uid:\t40002\t40002\t40002\t40002\n"
                     "Gid:\t40003\t40003\t40003\t40003\n",
            "exeTarget":profile["exeTarget"],
            "exeSha256":EXE_SHA,
            "scriptSha256":SRC_SHA,
        }
        self.assertFalse(provenance.inspect_snapshot(profile,snapshot)["workerExecutableAttested"])
        for key,val in (
            ("cgroup","0::/system.slice/evil.service\n"),
            ("cgroup","0::"+profile["cgroup"]+" (deleted)\n"),
            ("cmdline",snapshot["cmdline"]+b"--extra\x00"),
            ("cmdline",b"python -c exit(0)\x00"),
            ("status",snapshot["status"].replace("40002\t40002\t40002","0\t40002\t40002")),
            ("exeTarget",profile["exeTarget"]+" (deleted)"),
            ("exeSha256","f"*64),
            ("scriptSha256","e"*64),
        ):
            obj={**snapshot,key:val}
            with self.subTest(key=key,val=str(val)[:26]),self.assertRaises(provenance.ProvenanceError):
                provenance.inspect_snapshot(profile,obj)
        with self.assertRaises(provenance.ProvenanceError):
            provenance.inspect_snapshot(profile,{**snapshot,"untrusted":True})

    def test_nonroot_cross_uid_proc_access_and_changed_starttime_always_block(self):
        p=manifest()["profiles"]["worker"]
        with patch.object(provenance.pidfd,"pin_broker_process",
                          side_effect=provenance.pidfd.PidfdError("EACCES")),\
             patch.object(provenance,"_read_proc") as reader:
            with self.assertRaises(provenance.ProvenanceError):
                provenance.inspect_live_process(p)
            reader.assert_not_called()
        guard=MagicMock()
        guard.assert_live.return_value=False
        with patch.object(provenance.pidfd,"pin_broker_process",return_value=guard),\
             patch.object(provenance.audit,"read_broker_start_ticks",
                          side_effect=[p["startTicks"],p["startTicks"]+1]),\
             patch.object(provenance,"_read_proc",return_value=b""),\
             patch.object(provenance,"_hash_executable",return_value=EXE_SHA),\
             patch.object(provenance,"_hash_fixed_source",return_value=SRC_SHA):
            with self.assertRaises(provenance.ProvenanceError):
                provenance.inspect_live_process(p)
            guard.close.assert_called_once()

    def test_negative_process_attestation_report_always_false(self):
        report=provenance.denial()
        for key in ("workerExecutableAttested","brokerExecutableAttested",
                    "trustedRootManifestAttested","cgroupAttested",
                    "nativePublicationAuthorized","launchAuthorized",
                    "writePermissionGranted","releaseReady","approvedForProduction"):
            self.assertIs(report[key],False)

if __name__=="__main__":
    unittest.main()
