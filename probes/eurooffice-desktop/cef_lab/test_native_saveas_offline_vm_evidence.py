"""GD31J27ac deferred pure in-memory wheel and OS evidence regression tests.

No real package installation, systemd, root service, procfs or user docs.
These tests are authored for the later offline test stage, NOT executed.
"""
import base64
import csv
import hashlib
import io
import json
import os
from pathlib import Path
import sys
import unittest
import zipfile
from unittest.mock import patch

sys.path.insert(0,str(Path(__file__).resolve().parent))
import native_saveas_offline_vm_evidence as vm
import native_saveas_offline_sbom_evidence as sbom
import test_native_saveas_offline_sbom_evidence as previous_test


def sha(body):
    return hashlib.sha256(body).hexdigest()


def wheel(entries=None, *, extra_record=None, symlink=False):
    files=dict(entries or {
        "demo/__init__.py":b"hello offline world\n",
        "demo-1.0.dist-info/WHEEL":b"Wheel-Version: 1.0\nGenerator: offline-test\n",
        "demo-1.0.dist-info/METADATA":b"Metadata-Version: 2.1\nName: demo\nVersion: 1.0\n",
    })
    rows=[]
    for path,body in sorted(files.items()):
        digest=base64.urlsafe_b64encode(hashlib.sha256(body).digest()).rstrip(b"=").decode("ascii")
        rows.append([path,"sha256="+digest,str(len(body))])
    record_path="demo-1.0.dist-info/RECORD"
    rows.append([record_path,"",""])
    if extra_record is not None:
        rows=extra_record
    record=io.StringIO(newline="")
    writer=csv.writer(record,lineterminator="\n")
    writer.writerows(rows)
    files[record_path]=record.getvalue().encode()
    output=io.BytesIO()
    with zipfile.ZipFile(output,"w",compression=zipfile.ZIP_STORED) as z:
        for name,body in files.items():
            info=zipfile.ZipInfo(name)
            info.create_system=3
            info.external_attr=(
                (0o120777 if symlink and name=="demo/__init__.py" else 0o100644)<<16)
            z.writestr(info,body)
    return output.getvalue()


def sbom_catalog(blob):
    catalog=previous_test.catalog()
    catalog["wheels"]=[
        {"name":"demo","version":"1.0",
         "filename":"demo-1.0-py3-none-any.whl",
         "sha256":sha(blob)},
    ]
    return catalog


class WheelArchiveTests(unittest.TestCase):
    def test_correct_wheel_has_exact_record_and_returns_denial_only(self):
        archive=wheel()
        receipt=vm.inspect_wheel_record(
            archive,"demo-1.0-py3-none-any.whl",sha(archive))
        self.assertEqual("requires-independent-review",receipt["state"])
        self.assertTrue(receipt["wheelRecordObserved"])
        self.assertFalse(receipt["dependencyClosureAttested"])
        self.assertFalse(receipt["nativePublicationAuthorized"])
        self.assertFalse(receipt["releaseReady"])

    def test_tampered_wheel_digest_or_unlisted_file_and_bad_record(self):
        archive=wheel()
        with self.assertRaises(vm.VmEvidenceError):
            vm.inspect_wheel_record(archive,"demo-1.0-py3-none-any.whl",sha(b"wrong"))
        with self.assertRaises(vm.VmEvidenceError):
            vm.inspect_wheel_record(
                wheel(extra_record=[
                    ["demo/__init__.py","sha256=untrusted","20"],
                    ["demo-1.0.dist-info/RECORD","",""],
                ]),"demo-1.0-py3-none-any.whl",
                sha(wheel(extra_record=[
                    ["demo/__init__.py","sha256=untrusted","20"],
                    ["demo-1.0.dist-info/RECORD","",""],
                ])))
        unsafe=wheel(entries={"../outside":b"x"})
        with self.assertRaises(vm.VmEvidenceError):
            vm.inspect_wheel_record(unsafe,"demo-1.0-py3-none-any.whl",sha(unsafe))
        symlink=wheel(symlink=True)
        with self.assertRaises(vm.VmEvidenceError):
            vm.inspect_wheel_record(symlink,"demo-1.0-py3-none-any.whl",sha(symlink))

    def test_duplicated_record_entry_or_missing_record_is_refused(self):
        blob=wheel()
        with zipfile.ZipFile(io.BytesIO(blob)) as z:
            files={name:z.read(name) for name in z.namelist()}
        files.pop("demo-1.0.dist-info/RECORD")
        result=io.BytesIO()
        with zipfile.ZipFile(result,"w") as z:
            for name,body in files.items():
                z.writestr(name,body)
        data=result.getvalue()
        with self.assertRaises(vm.VmEvidenceError):
            vm.inspect_wheel_record(data,"demo-1.0-py3-none-any.whl",sha(data))
        record="demo-1.0.dist-info/RECORD"
        altered=wheel(extra_record=[
            ["demo/__init__.py","sha256="+base64.urlsafe_b64encode(
                hashlib.sha256(b"hello offline world\n").digest()).decode().rstrip("="),"20"],
            ["demo/__init__.py","sha256="+base64.urlsafe_b64encode(
                hashlib.sha256(b"hello offline world\n").digest()).decode().rstrip("="),"20"],
            [record,"",""],
        ])
        with self.assertRaises(vm.VmEvidenceError):
            vm.inspect_wheel_record(altered,"demo-1.0-py3-none-any.whl",sha(altered))

    def test_archive_entry_count_and_uncompressed_size_limits(self):
        blob=wheel(entries={"demo/payload":b"a"*(vm.MAX_UNCOMPRESSED_BYTES+1)})
        with self.assertRaises(vm.VmEvidenceError):
            vm.inspect_wheel_record(blob,"demo-1.0-py3-none-any.whl",sha(blob))


class OfflineOSSnapshotTests(unittest.TestCase):
    def snapshot(self,catalog):
        expected=sbom.expected_effective_unit
        return {
            "schema":vm.SNAPSHOT_SCHEMA,
            "mode":"external-offline-observation-untrusted",
            "wheels":[w["filename"] for w in catalog["wheels"]],
            "elfNeeded":catalog["elfNeeded"],
            "loadedElf":sorted(catalog["elf"]),
            "effectiveUnits":{role:expected(role) for role in ("broker","worker")},
            "processes":{
                "broker":{"pid":32001,"startTicks":123456,"uid":40001,"gid":40003},
                "worker":{"pid":33001,"startTicks":123457,"uid":40002,"gid":40003},
            },
        }

    def test_exact_external_snapshot_observed_never_attested(self):
        archive=wheel()
        catalog=sbom_catalog(archive)
        report=vm.inspect_external_snapshot(
            catalog,self.snapshot(catalog),
            expected_processes=self.snapshot(catalog)["processes"])
        self.assertTrue(report["externalFactsCompared"])
        self.assertFalse(report["osEvidenceAttested"])
        self.assertFalse(report["launchAuthorized"])
        self.assertFalse(report["releaseReady"])

    def test_missing_extra_elf_unit_or_changed_worker_identity_deny(self):
        archive=wheel()
        catalog=sbom_catalog(archive)
        base=self.snapshot(catalog)
        import copy
        for role,key,bad in (
            ("worker","uid",0),("worker","pid",33002),
            ("broker","startTicks",2),("worker","gid",40001),
        ):
            changed=copy.deepcopy(base)
            changed["processes"][role][key]=bad
            with self.subTest(role=role,key=key),self.assertRaises(vm.VmEvidenceError):
                vm.inspect_external_snapshot(catalog,changed,
                                              expected_processes=base["processes"])
        for change in ("extra_lib","dropin","enabled","false_proof","extra_field"):
            changed=copy.deepcopy(base)
            if change=="extra_lib":
                changed["loadedElf"].append("/tmp/evil.so")
            elif change=="dropin":
                changed["effectiveUnits"]["worker"]["DropInPaths"]="/tmp/evil.conf"
            elif change=="enabled":
                changed["effectiveUnits"]["broker"]["UnitFileState"]="enabled"
            elif change=="false_proof":
                changed["osEvidenceAttested"]=True
            else:
                changed["unknown"]="x"
            with self.subTest(change=change),self.assertRaises(vm.VmEvidenceError):
                vm.inspect_external_snapshot(catalog,changed,
                                              expected_processes=base["processes"])

    def test_signature_is_verified_in_composite_review_but_no_release(self):
        archive=wheel()
        catalog=sbom_catalog(archive)
        raw=(json.dumps(catalog,sort_keys=True,separators=(",",":"))+"\n").encode()
        manifest=previous_test.legacy()
        public=b"P"*32
        signature=b"S"*64
        with patch.object(sbom,"_verify_ed25519") as checked:
            result=vm.review_offline_vm_evidence(
                raw,signature,public,
                expected_key_sha256=sha(public),expected_catalog_sha256=sha(raw),
                expected_generation=7,legacy_manifest=manifest,
                wheel_archives={"demo-1.0-py3-none-any.whl":archive},
                snapshot=self.snapshot(catalog),
                expected_processes=self.snapshot(catalog)["processes"])
            checked.assert_called_once_with(public,signature,raw)
        self.assertTrue(result["detachedSignatureVerified"])
        self.assertTrue(result["wheelRecordObserved"])
        self.assertTrue(result["externalFactsCompared"])
        self.assertFalse(result["osEvidenceAttested"])
        self.assertFalse(result["releaseReady"])
        with patch.object(sbom,"_verify_ed25519",
                          side_effect=sbom.EvidenceError("bad signature")):
            with self.assertRaises(vm.VmEvidenceError):
                vm.review_offline_vm_evidence(
                    raw,signature,public,
                    expected_key_sha256=sha(public),expected_catalog_sha256=sha(raw),
                    expected_generation=7,legacy_manifest=manifest,
                    wheel_archives={"demo-1.0-py3-none-any.whl":archive},
                    snapshot=self.snapshot(catalog),
                    expected_processes=self.snapshot(catalog)["processes"])


if __name__=="__main__":
    unittest.main()
