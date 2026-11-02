"""GD31J27aa deferred source/unit/dependency pin contracts, NO VM execution.

Synthetic hashes and bodies only. This does not run systemd, read real
system files, trust runtime imports or authorize SaveAs.
"""
import hashlib
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0,str(Path(__file__).resolve().parent))
import native_saveas_dependency_unit_pins as pins

def sha(data):
    return hashlib.sha256(data).hexdigest()

MODULES={
    "native_saveas_broker_listener":"import native_saveas_worker_dialer\n",
    "native_saveas_worker_dialer":"import native_saveas_fd_worker\n",
    "native_saveas_fd_worker":"import native_saveas_sandbox_observer\n",
    "native_saveas_sandbox_observer":"import os\n",
}
UNIT_TEMPLATE=b"""[Unit]
Description=GETDOC synthetic lab denial-only
RefuseManualStart=yes
ConditionPathExists=/run/getdoc-gd31j27aa-disposable/APPROVAL_NEVER_AUTOCREATE
[Service]
Type=exec
User=getdoc-lab-broker
Group=getdoc-lab-ipc
ExecStart=/usr/bin/false
NoNewPrivileges=yes
PrivateNetwork=yes
PrivateMounts=yes
ProtectSystem=strict
ProtectHome=yes
RestrictAddressFamilies=AF_UNIX
RestrictSUIDSGID=yes
CapabilityBoundingSet=
AmbientCapabilities=
"""
UNIT_WORKER=UNIT_TEMPLATE.replace(b"getdoc-lab-broker",b"getdoc-lab-worker")

def manifest():
    return {
        "schema":pins.MANIFEST_SCHEMA,
        "mode":"inspect_only","laboratoryOnly":True,
        "publishEnabled":False,
        "modules":{key:sha(body.encode()) for key,body in MODULES.items()},
        "sharedObjects":{
            "/usr/lib/x86_64-linux-gnu/libc.so.6":sha(b"synthetic libc")
        },
        "units":{
            "broker":sha(UNIT_TEMPLATE),
            "worker":sha(UNIT_WORKER),
        }
    }

class DependencyPinTests(unittest.TestCase):
    def test_strict_fixed_manifest_and_denial(self):
        m=manifest()
        raw=json.dumps(m,separators=(",",":"),sort_keys=True).encode()+b"\n"
        self.assertEqual(m,pins.parse_manifest(raw))
        self.assertEqual("requires-independent-review",pins.denial()["state"])
        for key in ("dependencyClosureAttested","unitLaunchAttested",
                    "runtimeLibrariesAttested","nativePublicationAuthorized",
                    "writePermissionGranted","releaseReady",
                    "approvedForProduction"):
            self.assertIs(pins.denial()[key],False)

    def test_manifest_rejects_missing_extra_local_modules_or_dynamic_library_paths(self):
        m=manifest()
        broken=[]
        x=json.loads(json.dumps(m));x["modules"].pop("native_saveas_fd_worker");broken.append(x)
        x=json.loads(json.dumps(m));x["modules"]["untrusted_plugin"]=sha(b"x");broken.append(x)
        x=json.loads(json.dumps(m));x["sharedObjects"]["/tmp/evil.so"]=sha(b"x");broken.append(x)
        x=json.loads(json.dumps(m));x["sharedObjects"]["/usr/lib/x86_64-linux-gnu/../../tmp/x.so"]=sha(b"x");broken.append(x)
        x=json.loads(json.dumps(m));x["sharedObjects"]={};broken.append(x)
        x=json.loads(json.dumps(m));x["units"]["worker"]="0"*64;broken.append(x)
        x=json.loads(json.dumps(m));x["publishEnabled"]=True;broken.append(x)
        x=json.loads(json.dumps(m));x["mode"]="publish";broken.append(x)
        x=json.loads(json.dumps(m));x["unknown"]="x";broken.append(x)
        for x in broken:
            with self.subTest(item=str(x)[:50]),self.assertRaises(pins.DependencyError):
                pins.validate_manifest(x)
        valid=json.dumps(m,separators=(",",":")).encode()+b"\n"
        dup=valid.replace(b'"mode":"inspect_only"',b'"mode":"inspect_only","mode":"publish"')
        for raw in (dup,valid.rstrip(b"\n"),valid+valid,
                    b"x"*(pins.MAX_MANIFEST_BYTES+1),b'{"x":NaN}\n'):
            with self.subTest(raw=raw[:30]),self.assertRaises(pins.DependencyError):
                pins.parse_manifest(raw)

    def test_source_import_graph_closed_and_rejects_execution_escape(self):
        m=manifest()
        for key,body in MODULES.items():
            self.assertIs(pins.verify_local_source(key,body.encode(),m),
                          False)
        for item in ("import missing_module\n",
                     "import native_saveas_hidden\n",
                     "from importlib import import_module\n",
                     "__import__('socket')\n",
                     "exec('print(1)')\n"):
            with self.subTest(item=item),self.assertRaises(pins.DependencyError):
                pins.check_static_imports(item.encode(),pins.MODULE_NAMES)
        with self.assertRaises(pins.DependencyError):
            pins.verify_local_source(
                "native_saveas_fd_worker",b"tampered",m)

    def test_exact_systemd_units_refuse_weaker_policy_or_write_authority(self):
        m=manifest()
        self.assertIs(pins.verify_unit_bytes("broker",UNIT_TEMPLATE,m),False)
        self.assertIs(pins.verify_unit_bytes("worker",UNIT_WORKER,m),False)
        for old,new in (
            (b"PrivateNetwork=yes",b"PrivateNetwork=no"),
            (b"ProtectSystem=strict",b"ProtectSystem=no"),
            (b"CapabilityBoundingSet=",b"CapabilityBoundingSet=CAP_SYS_ADMIN"),
            (b"RefuseManualStart=yes",b"RefuseManualStart=no"),
            (b"User=getdoc-lab-broker",b"User=root"),
            (b"ExecStart=/usr/bin/false",b"ExecStart=/usr/bin/python3 -c print(1)"),
        ):
            data=UNIT_TEMPLATE.replace(old,new)
            with self.subTest(new=new),self.assertRaises(pins.DependencyError):
                pins.verify_unit_bytes("broker",data,m)

    def test_fixed_file_allowlist_contains_only_manifest_source_unit_or_lib(self):
        valid=[
            pins.FIXED_MANIFEST,
            pins.MODULE_DIR+"/native_saveas_fd_worker.py",
            pins.UNIT_PATHS["broker"],
            "/usr/lib/x86_64-linux-gnu/libc.so.6",
        ]
        for value in valid:
            with self.subTest(value=value):
                self.assertTrue(pins._fixed_components(value))
        for bad in (
            pins.MODULE_DIR+"/unknown.py",
            pins.MODULE_DIR+"/../secrets.conf",
            "/etc/passwd",
            "/tmp/user.docx",
            "/proc/self/maps",
            "/etc/systemd/system/random.service",
        ):
            with self.subTest(bad=bad),self.assertRaises(pins.DependencyError):
                pins._fixed_components(bad)

    def test_unlisted_runtime_shared_object_is_refused_not_silently_ignored(self):
        m=manifest()
        paths=tuple(sorted(m["sharedObjects"]))
        report=pins.compare_observed_library_paths(m,paths)
        self.assertEqual("requires-independent-review",report["state"])
        self.assertIs(report["runtimeLibrariesAttested"],False)
        for maps in (
            (),
            paths+("/tmp/plugin.so",),
            ("/usr/lib/x86_64-linux-gnu/libother.so.1",),
            ("/usr/lib/x86_64-linux-gnu/libc.so.6 (deleted)",),
        ):
            with self.subTest(maps=maps),self.assertRaises(pins.DependencyError):
                pins.compare_observed_library_paths(m,maps)

    def test_dynamic_loaded_library_is_not_claimed_verified_by_source_allowlist(self):
        m=manifest()
        snapshot={**{pins.MODULE_DIR+"/"+key+".py":digest
                      for key,digest in m["modules"].items()},
                  **m["sharedObjects"], **{pins.UNIT_PATHS[role]:sha
                                           for role,sha in m["units"].items()}}
        report=pins.inspect_pinned_digest_snapshot(m,snapshot)
        self.assertTrue(report["pinnedFilesObserved"])
        self.assertFalse(report["dependencyClosureAttested"])
        with self.assertRaises(pins.DependencyError):
            pins.inspect_pinned_digest_snapshot(m,{**snapshot,"/tmp/evil.so":sha(b"x")})
        with self.assertRaises(pins.DependencyError):
            pins.inspect_pinned_digest_snapshot(m,{**snapshot,"/usr/lib/x86_64-linux-gnu/libc.so.6":sha(b"bad")})

if __name__=="__main__":
    unittest.main()
