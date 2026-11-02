"""GD31J27aa static deny-only source contract; NOT a VM check."""
from pathlib import Path
import unittest

LAB=Path(__file__).resolve().parent
ROOT=LAB.parents[2]

class OfflineDependencySourceContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source=(LAB/"native_saveas_dependency_unit_pins.py").read_text("utf8")
        cls.runner=(ROOT/"scripts/office_acceptance.py").read_text("utf8")
        cls.cmake=(LAB/"CMakeLists.txt").read_text("utf8")
        cls.worker=(LAB/"native_saveas_fd_worker.py").read_text("utf8")
        cls.listener=(LAB/"native_saveas_broker_listener.py").read_text("utf8")
        cls.broker=(LAB/"native_saveas_fd_broker.py").read_text("utf8")
        cls.units=[(LAB/f"getdoc-gd31j27aa-{role}.blocked.service").read_text("utf8")
                   for role in ("broker","worker")]

    def test_exact_fixed_module_root_only_pin_and_no_authority(self):
        for token in (
            'FIXED_MANIFEST="/etc/getdoc-lab/gd31j27aa-dependencies.json"',
            'MODULE_DIR="/opt/getdoc-lab/bin"',
            'UNIT_PATHS={','MODULE_NAMES=frozenset((',
            'object_pairs_hook=_pairs_unique','def check_static_imports(',
            'ast.parse(', 'def verify_unit_bytes(',
            'def load_root_manifest(', 'def inspect_root_owned_files(',
            'def compare_observed_library_paths(',
            'os.O_NOFOLLOW','os.O_CLOEXEC','dir_fd=d',
            'required_mode=0o600', 'os.geteuid()!=0',
            'nativePublicationAuthorized":False',
            'dependencyClosureAttested":False',
            'unitLaunchAttested":False',
            'approvedForProduction":False',
        ):
            with self.subTest(token=token):
                self.assertIn(token,self.source)

    def test_blocked_services_and_no_production_integration(self):
        for role,data in zip(("broker","worker"),self.units):
            self.assertIn("RefuseManualStart=yes",data)
            self.assertIn("ExecStart=/usr/bin/false",data)
            self.assertIn("ConditionPathExists=/run/getdoc-gd31j27aa-disposable/APPROVAL_NEVER_AUTOCREATE",data)
            self.assertIn("CapabilityBoundingSet=",data)
            self.assertIn("User=getdoc-lab-"+role,data)
            self.assertNotIn("WantedBy=",data)
            self.assertNotIn("[Install]",data)
        for runtime in (self.worker,self.listener,self.broker):
            self.assertNotIn("native_saveas_dependency_unit_pins",runtime)
        for forbidden in ("subprocess.","os.setuid(","os.setgid(","os.chown(",
                          "os.chmod(","os.unlink(","os.rename(","os.replace(",
                          "os.fork(","os.execv(","os.kill(","pidfd_getfd(",
                          "os.unshare(","os.setns("):
            self.assertNotIn(forbidden,self.source)
        self.assertIn("GD31J27AA_BLOCKED:",self.cmake)
        self.assertIn("native-saveas-dependency-unit-source-tests",self.runner)


if __name__=="__main__":
    unittest.main()
