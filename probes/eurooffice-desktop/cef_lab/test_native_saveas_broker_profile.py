"""GD31J27m: sandbox policy is a template, never a runtime attestation."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_saveas_broker_profile as policy


class BrokerProfileTests(unittest.TestCase):
    def test_complete_draft_still_denies_launch_publication_and_release(self):
        config = policy.draft_profile(revision="a"*40,
                                      broker_uid=40001, worker_uid=40002)
        outcome = policy.inspect_profile(config)
        self.assertEqual("blocked", outcome["state"])
        self.assertFalse(outcome["brokerLaunchAuthorized"])
        self.assertFalse(outcome["kernelIsolationAttested"])
        self.assertFalse(outcome["nativePublicationAuthorized"])
        self.assertFalse(outcome["releaseReady"])
        self.assertEqual("external-operator-required", outcome["nextGate"])
        self.assertEqual("disabled", config["filesystem"]["publishOperation"])
        self.assertEqual(["source-readonly", "scratch-dir-readonly",
                          "final-dir-readonly"], config["transport"]["fdRoles"])

    def test_relaxations_unknown_fields_and_uid_collision_fail(self):
        mutations = (
            ("same_uid", lambda x: x.update(workerUid=x["brokerUid"])),
            ("root_worker", lambda x: x.update(workerUid=0)),
            ("root_broker", lambda x: x.update(brokerUid=0)),
            ("wrong_revision", lambda x: x.update(revision="latest")),
            ("network", lambda x: x["kernel"].update(networkDisabled=False)),
            ("seccomp", lambda x: x["kernel"].update(seccompFilter=False)),
            ("ptrace", lambda x: x["kernel"].update(ptraceDenied=False)),
            ("group_writable", lambda x: x["filesystem"].update(scratchMode="0770")),
            ("write_allowed", lambda x: x["filesystem"].update(publishOperation="publish")),
            ("extra", lambda x: x["filesystem"].update(homeDirectory="/home/user")),
            ("extra_top", lambda x: x.update(approvedForProduction=True)),
            ("reordered_fds", lambda x: x["transport"].update(
                fdRoles=["final-dir-readonly","source-readonly","scratch-dir-readonly"])),
        )
        for name, mutate in mutations:
            with self.subTest(name=name):
                config = policy.draft_profile(revision="b"*40,
                                              broker_uid=40101, worker_uid=40102)
                mutate(config)
                with self.assertRaises(policy.ProfileError):
                    policy.inspect_profile(config)

    def test_static_profile_cannot_be_presented_as_verified_kernel_state(self):
        config = policy.draft_profile(revision="c"*40,
                                      broker_uid=40201, worker_uid=40202)
        with self.assertRaises(policy.ProfileError):
            config["kernel"]["sandboxAttested"] = True
            policy.inspect_profile(config)


if __name__ == "__main__":
    unittest.main()
