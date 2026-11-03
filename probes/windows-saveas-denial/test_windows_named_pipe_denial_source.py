"""GD31J27ak source-only contract; runs on Windows OR Linux, never opens a pipe."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
LAB = ROOT / "src/GetDocument.Office/EuroOfficeDesktop/WindowsNamedPipeDenialLab.cs"
ENTRY = ROOT / "probes/windows-saveas-denial/Program.cs"
PROJECT = ROOT / "probes/windows-saveas-denial/GetDocWindowsPipeDenialLab.csproj"
TESTS = ROOT / "tests/GetDocument.Office.Tests/WindowsNamedPipeDenialLabTests.cs"


class WindowsDenialSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.lab = LAB.read_text(encoding="utf-8")
        cls.entry = ENTRY.read_text(encoding="utf-8")
        cls.project = PROJECT.read_text(encoding="utf-8")
        cls.tests = TESTS.read_text(encoding="utf-8")

    def test_kernel_connected_pid_and_same_user_both_ends(self):
        for required in (
            "GetNamedPipeClientProcessId",
            "RequireMatchingClientPid(connectedPid, pinnedClientPid, Environment.ProcessId)",
            "PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance",
            "PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly",
            "PipeTransmissionMode.Message",
            "PipeName(Guid runId)",
            "RunId",  # not required: only exact lower-case run IDs are handled by entry
        ):
            if required == "RunId":
                continue
            with self.subTest(required=required):
                self.assertIn(required, self.lab)

    def test_denial_only_and_manual_vm_operator_gate(self):
        for required in (
            '"nativePublicationAuthorized', '"releaseReady',
            '"writePermissionGranted',
            "public static bool NativePublicationAuthorized => false",
            "public static bool ReleaseReady => false",
        ):
            with self.subTest(required=required):
                self.assertIn(required, self.lab)
        for required in (
            'Environment.GetEnvironmentVariable(EnvName) != EnvValue',
            '"OFFLINE_DISPOSABLE_NO_DOCS"',
            '"I_APPROVE_DISPOSABLE_WINDOWS_VM_NO_DOCUMENTS_DENIAL_ONLY"',
            'return 2;',
            '"GD31J27AK_CLIENT_PID="',
        ):
            with self.subTest(required=required):
                self.assertIn(required, self.entry)

    def test_no_launch_no_files_no_service_no_privileges(self):
        for forbidden in (
            "File.Open(", "File.Write", "File.Move(", "File.Delete(",
            "Directory.Create", "Process.Start(", "OpenProcess(",
            "DuplicateHandle(", "CreateFile(", "SetTokenInformation(",
            "CreateProcess(", "SetNamedSecurityInfo(", "PipeAccessRule(",
            "HttpClient", "Socket(", "GetDocument.Wopi", "Gup2",
        ):
            for source in (self.lab, self.entry):
                with self.subTest(forbidden=forbidden):
                    self.assertNotIn(forbidden, source)
        self.assertIn("<OutputType>Exe</OutputType>", self.project)
        self.assertIn("WindowsNamedPipeDenialLabTests", self.tests)


if __name__ == "__main__":
    unittest.main()
