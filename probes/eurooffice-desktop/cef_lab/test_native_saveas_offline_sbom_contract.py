"""GD31J27ab source-only security contracts. NOT executable attestation."""
from pathlib import Path
import unittest

LAB=Path(__file__).resolve().parent
ROOT=LAB.parents[2]

class SignedSbomSourceContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source=(LAB/"native_saveas_offline_sbom_evidence.py").read_text("utf8")
        cls.runner=(ROOT/"scripts/office_acceptance.py").read_text("utf8")
        cls.cmake=(LAB/"CMakeLists.txt").read_text("utf8")
        cls.worker=(LAB/"native_saveas_fd_worker.py").read_text("utf8")
        cls.broker=(LAB/"native_saveas_fd_broker.py").read_text("utf8")
        cls.listener=(LAB/"native_saveas_broker_listener.py").read_text("utf8")

    def test_signature_verified_by_real_optional_backend_not_json_claim(self):
        for needle in (
            "from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey",
            "cls.from_public_bytes(public_key).verify(signature,raw)",
            "hmac.compare_digest(", "expected_pubkey_sha256",
            "expected_catalog_sha256","expected_generation",
            "_verify_ed25519(public_key,signature,raw)",
            "object_pairs_hook=_pairs",
            "sort_keys=True", "allow_nan=False",
            "def inspect_signed_catalog(",
            "def inspect_effective_unit_snapshot(",
            "def compare_inventory(",
            "compare_legacy_pins(document,legacy_manifest)",
            '"signatureAttested":False',
            '"dependencyClosureAttested":False',
            '"effectiveUnitAttested":False',
            '"releaseReady":False',
        ):
            with self.subTest(needle=needle):
                self.assertIn(needle,self.source)

    def test_deny_only_path_never_wires_production_or_uses_exec(self):
        for content in (self.worker,self.broker,self.listener,self.cmake):
            self.assertNotIn("native_saveas_offline_sbom_evidence",content)
        for token in (
            "subprocess.", "os.system(", "os.setuid(", "os.setgid(",
            "os.chown(", "os.chmod(", "os.unlink(", "os.rename(",
            "os.replace(", "os.open(", "socket.socket(", "os.execv(",
            "os.fork(", "os.unshare(", "os.kill(",
        ):
            self.assertNotIn(token,self.source)
        self.assertIn("GD31J27AB_BLOCKED:",self.cmake)
        self.assertIn("native-saveas-offline-sbom-evidence-tests",self.runner)
        self.assertIn("native-saveas-offline-sbom-source-tests",self.runner)

if __name__=="__main__":
    unittest.main()
