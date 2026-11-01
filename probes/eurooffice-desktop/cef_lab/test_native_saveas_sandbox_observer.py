"""GD31J27n: deferred tests. Read-only observed kernel facts != attestation.

No systemctl, service launch, CEF, VM, network or real filesystem modification.
"""
import sys
from pathlib import Path
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_saveas_sandbox_observer as observer

STATUS = """Name:\tgetdoc-lab
Uid:\t40002\t40002\t40002\t40002
Gid:\t40002\t40002\t40002\t40002
NoNewPrivs:\t1
Seccomp:\t2
Seccomp_filters:\t1
CapInh:\t0000000000000000
CapPrm:\t0000000000000000
CapEff:\t0000000000000000
CapBnd:\t0000000000000000
CapAmb:\t0000000000000000
TracerPid:\t0
"""
MOUNTS = """23 1 0:21 / / rw,relatime - tmpfs tmpfs rw
24 23 0:22 / /run rw,nosuid,nodev - tmpfs tmpfs rw
25 23 8:2 / /usr ro,relatime - ext4 /dev/vda2 ro
"""
OWN = {"mnt": 31001, "net": 31002}
PEER = {"mnt": 71001, "net": 71002}


class KernelObserverTests(unittest.TestCase):
    def test_valid_kernel_evidence_never_grants_permission(self):
        output = observer.inspect_observed(
            status_text=STATUS, mountinfo_text=MOUNTS,
            own_ns=OWN, peer_ns=PEER, expected_uid=40002, peer_uid=40001)
        self.assertEqual("requires-manual-review", output["reviewState"])
        self.assertTrue(output["kernelFactsObserved"])
        for field in ("releaseReady", "nativePublicationAuthorized",
                      "brokerLaunchAuthorized", "kernelIsolationAttested",
                      "approvedForProduction", "writePermissionGranted",
                      "nativeSaveProven", "documentVerified", "saveVerified"):
            self.assertIs(output[field], False)

    def test_root_same_uid_and_invalid_namespace_refused(self):
        cases = (
            {"expected_uid": 0},
            {"peer_uid": 40002},
            {"own_ns": {"mnt": 31001, "net": 71002}},
            {"peer_ns": {"mnt": 31001, "net": 71002}},
            {"own_ns": {"mnt": 0, "net": 31002}},
        )
        for changes in cases:
            with self.subTest(changes=changes), self.assertRaises(observer.ObservationError):
                kwargs = dict(status_text=STATUS, mountinfo_text=MOUNTS,
                              own_ns=OWN, peer_ns=PEER,
                              expected_uid=40002, peer_uid=40001)
                kwargs.update(changes)
                observer.inspect_observed(**kwargs)

    def test_security_flags_and_credentials_are_strict(self):
        variants = (
            STATUS.replace("NoNewPrivs:\t1", "NoNewPrivs:\t0"),
            STATUS.replace("Seccomp:\t2", "Seccomp:\t0"),
            STATUS.replace("Seccomp_filters:\t1", "Seccomp_filters:\t0"),
            STATUS.replace("CapEff:\t0000000000000000", "CapEff:\t0000000000000001"),
            STATUS.replace("CapPrm:\t0000000000000000", "CapPrm:\t0000000000000001"),
            STATUS.replace("CapBnd:\t0000000000000000", "CapBnd:\t0000000000000001"),
            STATUS.replace("CapAmb:\t0000000000000000", "CapAmb:\t0000000000000001"),
            STATUS.replace("TracerPid:\t0", "TracerPid:\t1"),
            STATUS.replace("40002\t40002\t40002\t40002",
                           "40002\t40002\t40002\t0"),
            STATUS + "Seccomp:\t2\n",
        )
        for status in variants:
            with self.subTest(status=status[-50:]), self.assertRaises(observer.ObservationError):
                observer.inspect_observed(
                    status_text=status, mountinfo_text=MOUNTS,
                    own_ns=OWN, peer_ns=PEER, expected_uid=40002,
                    peer_uid=40001)

    def test_network_mounts_and_unparseable_mountinfo_refused(self):
        for mounts in (
            MOUNTS + "31 23 0:33 / /mnt/shared rw - cifs //server/share rw\n",
            MOUNTS + "32 23 0:34 / /mnt/remote rw - nfs4 server:/data rw\n",
            MOUNTS + "33 23 0:35 / /mnt/bridge rw - 9p host rw\n",
            MOUNTS + "34 23 0:36 / /mnt/remote rw - fuse.sshfs x rw\n",
            "malformed-line-without-separator\n",
            "",
        ):
            with self.subTest(mounts=mounts[-55:]), self.assertRaises(observer.ObservationError):
                observer.inspect_observed(
                    status_text=STATUS, mountinfo_text=mounts,
                    own_ns=OWN, peer_ns=PEER, expected_uid=40002,
                    peer_uid=40001)

    def test_observer_cannot_claim_hidden_mounts_or_secret_free_os(self):
        report = observer.inspect_observed(
            status_text=STATUS, mountinfo_text=MOUNTS, own_ns=OWN,
            peer_ns=PEER, expected_uid=40002, peer_uid=40001)
        self.assertFalse(report["corporateMountsAbsentAttested"])
        self.assertFalse(report["seccompFilterSemanticsAttested"])
        self.assertFalse(report["inheritedFdIsolationAttested"])


if __name__ == "__main__":
    unittest.main()
