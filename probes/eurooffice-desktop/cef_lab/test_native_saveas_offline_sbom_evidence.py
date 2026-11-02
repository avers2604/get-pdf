"""GD31J27ab: deferred tests for detached signed, canonical offline SBOM.

No real OS/systemd, third-party keys, user documents, runtime process calls or
test execution. All public keys, signatures and SHA pins here are synthetic.
"""
import hashlib
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0,str(Path(__file__).resolve().parent))
import native_saveas_offline_sbom_evidence as sbom
import native_saveas_dependency_unit_pins as previous

def digest(body):
    return hashlib.sha256(body).hexdigest()

def legacy():
    modules={name:digest(("synthetic "+name).encode("ascii"))
             for name in previous.MODULE_NAMES}
    return {"schema":previous.MANIFEST_SCHEMA, "mode":"inspect_only",
            "laboratoryOnly":True,"publishEnabled":False,
            "modules":modules,
            "sharedObjects":{"/usr/lib/x86_64-linux-gnu/libc.so.6":digest(b"ELF")},
            "units":{"broker":digest(b"disabled broker"),
                     "worker":digest(b"disabled worker")}}

def catalog():
    base=legacy()
    return {
        "schema":sbom.SCHEMA,"generation":7,
        "mode":"inspect_only","laboratoryOnly":True,"publishEnabled":False,
        "modules":base["modules"],
        "wheels":[{"name":"cryptography","version":"45.0.1",
                   "filename":"cryptography-45.0.1-py3-none-any.whl",
                   "sha256":digest(b"synthetic wheel")}],
        "elf":base["sharedObjects"],
        "elfNeeded":{"/usr/lib/x86_64-linux-gnu/libc.so.6":[]},
        "importEdges":{name:[] for name in base["modules"]},
        "units":base["units"],
    }

def raw(c):
    return (json.dumps(c,sort_keys=True,separators=(",",":"),
                       ensure_ascii=True)+"\n").encode("ascii")

class SignedCatalogContracts(unittest.TestCase):
    def test_exact_canonical_catalog_and_root_operator_pins(self):
        c=catalog()
        self.assertEqual(c,sbom.parse_catalog(raw(c)))
        self.assertFalse(sbom.denial()["releaseReady"])
        self.assertFalse(sbom.denial()["signatureAttested"])
        self.assertFalse(sbom.denial()["dependencyClosureAttested"])
        self.assertFalse(sbom.denial()["effectiveUnitAttested"])
        self.assertEqual("requires-independent-review",sbom.denial()["state"])
        self.assertEqual("declared-files-only",sbom.CLOSURE_SCOPE)

    def test_missing_extra_and_unsafe_wheels_or_elf_are_denied(self):
        c=catalog()
        cases=[]
        for field,value in (
            ("publishEnabled",True),("mode","publish"),("generation",0),
            ("wheels",[{"name":"../../evil","version":"1.0",
                        "filename":"../evil.whl","sha256":digest(b"bad")}]),
        ):
            x=json.loads(json.dumps(c));x[field]=value;cases.append(x)
        x=json.loads(json.dumps(c));x["wheels"][0]["sha256"]="0"*64;cases.append(x)
        x=json.loads(json.dumps(c));x["wheels"].append(x["wheels"][0]);cases.append(x)
        x=json.loads(json.dumps(c));x["elfNeeded"]["/tmp/evil.so"]=[];cases.append(x)
        x=json.loads(json.dumps(c));x["elfNeeded"]["/usr/lib/x86_64-linux-gnu/libc.so.6"]=["libunknown.so"];cases.append(x)
        x=json.loads(json.dumps(c));x["importEdges"]["unknown_plugin"]=[];cases.append(x)
        x=json.loads(json.dumps(c));x["importEdges"]["native_saveas_fd_worker"]=["unlisted_local"];cases.append(x)
        x=json.loads(json.dumps(c));x["modules"]["extra"]=digest(b"x");cases.append(x)
        x=json.loads(json.dumps(c));x["units"]["worker"]=digest(b"other");x["extra"]=True;cases.append(x)
        for bad in cases:
            with self.subTest(case=str(bad)[:55]),self.assertRaises(sbom.EvidenceError):
                sbom.validate_catalog(bad)

    def test_exact_json_rejects_whitespace_duplicate_nonfinite_and_trailing(self):
        body=raw(catalog())
        duplicate=body.replace(b'"generation":7',b'"generation":7,"generation":8')
        samples=(duplicate,body.replace(b'"generation":7',b'"generation": 7'),
                 body+b"\n",body.rstrip(b"\n"),b'{"x":NaN}\n',
                 b"x"*(sbom.MAX_CATALOG_BYTES+1))
        for sample in samples:
            with self.subTest(sample=sample[:35]),self.assertRaises(sbom.EvidenceError):
                sbom.parse_catalog(sample)

    def test_detached_ed25519_requires_independent_key_and_catalog_sha(self):
        body=raw(catalog())
        public=b"K"*32
        signature=b"S"*64
        def verifier(pub,sig,payload):
            self.assertEqual(public,pub)
            self.assertEqual(signature,sig)
            self.assertEqual(body,payload)
        with patch.object(sbom,"_verify_ed25519",side_effect=verifier) as checked:
            result=sbom.inspect_signed_catalog(
                body,signature,public,
                expected_pubkey_sha256=digest(public),
                expected_catalog_sha256=digest(body),
                expected_generation=7,legacy_manifest=legacy())
            checked.assert_called_once()
        self.assertTrue(result["detachedSignatureVerified"])
        self.assertFalse(result["signatureAttested"])
        self.assertFalse(result["launchAuthorized"])
        self.assertFalse(result["releaseReady"])
        for changes in (
            {"expected_pubkey_sha256":digest(b"wrong")},
            {"expected_catalog_sha256":digest(b"wrong")},
            {"expected_generation":8},
        ):
            kwargs=dict(expected_pubkey_sha256=digest(public),
                        expected_catalog_sha256=digest(body),
                        expected_generation=7,legacy_manifest=legacy())
            kwargs.update(changes)
            with self.subTest(changes=changes),patch.object(
                    sbom,"_verify_ed25519") as no_verify:
                with self.assertRaises(sbom.EvidenceError):
                    sbom.inspect_signed_catalog(body,signature,public,**kwargs)
                no_verify.assert_not_called()
        with patch.object(sbom,"_verify_ed25519",
                          side_effect=sbom.EvidenceError("invalid signature")):
            with self.assertRaises(sbom.EvidenceError):
                sbom.inspect_signed_catalog(
                    body,signature,public,
                    expected_pubkey_sha256=digest(public),
                    expected_catalog_sha256=digest(body),
                    expected_generation=7,legacy_manifest=legacy())

    def test_rfc8032_public_test_vector_when_optional_crypto_present(self):
        try:
            sbom._load_ed25519_backend()
        except ImportError:
            self.skipTest("No cryptography in offline test environment")
        public=bytes.fromhex(
            "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a")
        signature=bytes.fromhex(
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901"
            "55cb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b")
        sbom._verify_ed25519(public,signature,b"")
        with self.assertRaises(sbom.EvidenceError):
            sbom._verify_ed25519(public,signature,b"tampered")

    def test_ed25519_missing_optional_backend_fails_not_assumed_signed(self):
        with patch.object(sbom,"_load_ed25519_backend",
                          side_effect=ImportError("offline backend absent")):
            with self.assertRaises(sbom.EvidenceError):
                sbom._verify_ed25519(b"K"*32,b"S"*64,b"x")

    def test_catalog_must_match_older_fixed_root_hashes(self):
        c=catalog();old=legacy()
        self.assertIs(sbom.compare_legacy_pins(c,old),False)
        bad=json.loads(json.dumps(old))
        bad["modules"]["native_saveas_fd_worker"]=digest(b"mismatched")
        with self.assertRaises(sbom.EvidenceError):
            sbom.compare_legacy_pins(c,bad)

    def test_declared_local_import_edges_require_matching_source_sha_and_ast(self):
        c=catalog()
        src={name:b"import os\n" for name in c["modules"]}
        src["native_saveas_broker_listener"]=b"import native_saveas_fd_worker\n"
        c["importEdges"]["native_saveas_broker_listener"]=["native_saveas_fd_worker"]
        c["modules"]={name:digest(body) for name,body in src.items()}
        report=sbom.compare_local_import_graph(c,src)
        self.assertTrue(report["staticImportFactsObserved"])
        self.assertFalse(report["dependencyClosureAttested"])
        bad=json.loads(json.dumps(c))
        bad["importEdges"]["native_saveas_broker_listener"]=[]
        with self.assertRaises(sbom.EvidenceError):
            sbom.compare_local_import_graph(bad,src)
        src_bad=dict(src)
        src_bad["native_saveas_fd_worker"]=b"import malicious_plugin\n"
        with self.assertRaises(sbom.EvidenceError):
            sbom.compare_local_import_graph(c,src_bad)

    def test_effective_service_unit_fields_require_nonstartable_exact_snapshot(self):
        c=catalog()
        observations={role:sbom.expected_effective_unit(role)
                      for role in ("broker","worker")}
        report=sbom.inspect_effective_unit_snapshot(c,observations)
        self.assertTrue(report["effectiveUnitFactsObserved"])
        self.assertFalse(report["effectiveUnitAttested"])
        self.assertFalse(report["releaseReady"])
        for role,key,value in (
            ("broker","ExecStart","/usr/bin/python3"),
            ("broker","DropInPaths","/etc/systemd/system/evil.conf"),
            ("worker","NoNewPrivileges","no"),
            ("worker","User","root"),
            ("broker","UnitFileState","enabled"),
            ("broker","FragmentPath","/tmp/broker.service"),
        ):
            changed=json.loads(json.dumps(observations))
            changed[role][key]=value
            with self.subTest(role=role,key=key),self.assertRaises(sbom.EvidenceError):
                sbom.inspect_effective_unit_snapshot(c,changed)

    def test_independent_coverage_must_include_every_declared_wheel_and_mapping(self):
        c=catalog()
        names=("cryptography-45.0.1-py3-none-any.whl",)
        elf=("/usr/lib/x86_64-linux-gnu/libc.so.6",)
        self.assertFalse(sbom.compare_inventory(c,names,elf)["dependencyClosureAttested"])
        for wheels,libs in (((),elf),(names,()),
                            (names+("evil.whl",),elf),(names,elf+("/tmp/evil.so",))):
            with self.subTest(wheels=wheels,libs=libs),self.assertRaises(sbom.EvidenceError):
                sbom.compare_inventory(c,wheels,libs)


if __name__=="__main__":
    unittest.main()
