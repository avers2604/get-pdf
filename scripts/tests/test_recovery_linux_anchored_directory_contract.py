"""GD-31J.27bu: static-only Astra Linux anchored-directory contract.

NOT an Astra platform run, native openat2 test, .NET compilation or release
approval. No real recovery document is read, deleted or modified here.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
OFFICE = ROOT / "src/GetDocument.Office/LibreOffice"
SOURCE = OFFICE / "RecoveryLinuxAnchoredDirectoryAudit.cs"
PINNED = OFFICE / "RecoveryPinnedLinuxParentProbe.cs"
STORE = OFFICE / "OfficeRecoveryStore.cs"
COORD = OFFICE / "RecoverySharedVolumeCoordinator.cs"
ADAPTERS = OFFICE / "RecoveryCaptureIdentityAttestation.cs"
TESTS = ROOT / "tests/GetDocument.Office.Tests/RecoveryLinuxAnchoredDirectoryAuditTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class LinuxAnchoredDirectoryAuditContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = SOURCE.read_text(encoding="utf-8")
        cls.pinned = PINNED.read_text(encoding="utf-8")
        cls.store = STORE.read_text(encoding="utf-8")
        cls.coord = COORD.read_text(encoding="utf-8")
        cls.adapters = ADAPTERS.read_text(encoding="utf-8")
        cls.tests = TESTS.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")

    def test_root_anchored_without_path_based_child_reopens(self):
        for token in (
            'Open("/", ODirectory | ONoFollow | OCloexec)',
            "ReadExistingDirectoryEvidence(",
            "SyscallOpenAt2(OpenAt2Syscall,",
            "Resolve = ResolveBeneath | ResolveNoSymlinks |",
            "ResolveNoMagicLinks | ResolveNoXdev",
            "OpenAt2Syscall = 437",
            "MaxDepth = 32",
            "MaxPathBytes = 4096",
            "MaxComponentBytes = 255",
        ):
            with self.subTest(token=token):
                self.assertIn(token, self.source)

    def test_directory_metadata_requires_same_mount_and_private_owner(self):
        for token in (
            "RequireDirectoryPolicy(",
            "RecoveryPinnedLinuxParentProbe.RequirePrivateOwner(",
            "item.OwnerUid != 0 && item.OwnerUid != effectiveUid",
            "(item.Mode & 0x0012) != 0",
            "RequireStableChain(",
            "parentAtOpen != parentRecheck",
            "parentAtOpen.MountId != child.MountId",
            "parentAtOpen.FileId.DeviceOrVolume != child.FileId.DeviceOrVolume",
        ):
            self.assertIn(token, self.source)

    def test_xattr_inventory_is_bounded_read_only_with_acl_warning(self):
        for token in (
            "MaxXattrBytes = 8192", "MaxXattrNames = 64",
            "FListXattr(fd, null, 0)", "FListXattr(fd, bytes,",
            "if (read > required)", "system.posix_acl_access",
            "system.posix_acl_default", 'name.StartsWith("security."',
            'name.StartsWith("trusted."',
            "StrictUtf8.GetString(source, start, pos - start)",
            "SHA256.HashData(StrictUtf8.GetBytes(canonical))",
            "unique.OrderBy(x => x,", "NamesSha256",
            "internal bool CanAuthorizeCapture => false;",
        ):
            self.assertIn(token, self.source)

    def test_lease_retains_handles_and_cleans_failures_and_cancellation(self):
        for token in (
            "cancellationToken.ThrowIfCancellationRequested();",
            "owned.Add(root)", "owned.Add(child)",
            "transferred = true", "if (!transferred)",
            "owned[i].Dispose();",
            "Interlocked.Exchange(ref _handles, null)",
            "handles[i].Dispose();",
        ):
            self.assertIn(token, self.source)

    def test_no_mutations_and_no_insecure_fallback(self):
        for forbidden in (
            "Directory.CreateDirectory(", "File.Delete(",
            "Directory.Delete(", "File.Move(", "File.Write",
            "chmod(", "chown(", "setxattr(", "removexattr(",
            "openat(", "File.Open(", "File.OpenHandle(",
            "FileMode.Create",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.source)
        self.assertNotIn("RecoveryLinuxAnchoredDirectoryAudit.", self.store)
        self.assertNotIn("RecoveryLinuxAnchoredDirectoryAudit.", self.coord)
        self.assertIn("SupportsVerifiedRootAndLockIdentity => false;", self.adapters)

    def test_read_only_pinned_directory_uses_preopened_handle(self):
        self.assertIn("ReadExistingDirectoryEvidence(", self.pinned)
        self.assertIn("ReadHeldDirectoryEvidence(handle)", self.pinned)
        self.assertIn("DangerousAddRef(ref pinned)", self.pinned)

    def test_synthetic_mstest_cases_cover_fail_closed_boundaries(self):
        self.assertEqual(12, self.tests.count("[TestMethod]"))
        for token in (
            "TraversalRepeatedSeparatorAndNonAbsolutePathsAreDenied",
            "FinalDirectoryMustBeOwnerOnlyAndSameEffectiveUid",
            "XattrListReportsAclAndSecurityLabelsWithoutAssumingSafety",
            "XattrDigestIsOrderIndependentButDetectsSameCountNameReplacement",
            "MountAliasMismatchAndChangingAncestorEvidenceAreDenied",
            "NoLiveInspectionIsAllowedFromUnsupportedOrInvalidPath",
        ):
            self.assertIn(token, self.tests)

    def test_manual_platform_gates_and_offline_runner(self):
        step = 'CommandStep("recovery-linux-anchored-directory-source-tests"'
        self.assertEqual(1, self.runner.count(step))
        self.assertLess(self.runner.index(step),
                        self.runner.index('CommandStep("solution-restore"'))
        self.assertIn(
            '"-p", "test_recovery_linux_anchored_directory_contract.py"',
            self.runner)
        for gate in (
            "gd31j27bu-astra-root-chain-openat2-manual-e2e",
            "gd31j27bu-astra-xattr-acl-visibility-manual-e2e",
            "gd31j27bu-astra-mount-namespace-alias-manual-e2e",
            "gd31j27bu-astra-uid-permissions-cancel-manual-e2e",
        ):
            self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
