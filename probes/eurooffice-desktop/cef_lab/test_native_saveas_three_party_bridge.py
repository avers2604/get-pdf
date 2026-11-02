"""GD31J27s: source-only/deferred tests for 3-party deny-only FD bridge.

Do not run against working documents or under root. No actual UID switching,
systemd, native CEF, privileged fixture provisioning, or publication.
"""
from pathlib import Path
import json
import socket
import sys
import unittest
from unittest.mock import patch

LAB = Path(__file__).resolve().parent
sys.path.insert(0, str(LAB))
import native_saveas_three_party_bridge as bridge
import native_saveas_fd_provisioning as provisioning
import native_saveas_fd_broker as broker

def policy():
    return {
        "schema": bridge.POLICY_SCHEMA,
        "mode": "inspect_only",
        "laboratoryOnly": True,
        "workerUid": 40002,
        "brokerUid": 40001,
        "brokerGid": 40003,
        "supervisorUid": 0,
        "expectedSupervisorPid": 30001,
        "expectedWorkerPid": 30002,
        "expectedWorkerStartTicks": 987654,
        "allowPublication": False,
    }

def pins():
    return {"gd31j6_writer.docx": "a"*64, "gd31j6_calc.xlsx": "b"*64,
            "gd31j6_impress.pptx": "c"*64}

class StubSocket:
    family=socket.AF_UNIX
    type=socket.SOCK_SEQPACKET
    def __init__(self):
        self.shutdowns=[]
    def shutdown(self, how):
        self.shutdowns.append(how)

@unittest.skipUnless(sys.platform.startswith("linux"), "Linux-only SOCK_SEQPACKET denial relay")
class BridgeDenyOnlyTests(unittest.TestCase):
    def test_exact_policy_rejects_improper_accounts_or_writes(self):
        self.assertEqual("inspect_only", bridge.parse_static_policy(policy())["mode"])
        for key, value in (
            ("mode","publish"),("allowPublication",True),
            ("laboratoryOnly",False),("workerUid",40001),("brokerUid",0),
            ("supervisorUid",40004),("expectedSupervisorPid",0),
            ("expectedWorkerPid",-1),("expectedWorkerStartTicks",0),
            ("extra","yes"),
        ):
            item=policy();item[key]=value
            with self.subTest(key=key),self.assertRaises(bridge.BridgeError):
                bridge.parse_static_policy(item)

    def test_missing_vm_approval_denied_without_relay(self):
        with patch.object(bridge.provisioning, "relay_pinned_once") as relay:
            with self.assertRaises(bridge.BridgeError):
                bridge.bridge_inspect_only_once(
                    policy(), StubSocket(), StubSocket(),
                    pinned_hashes=pins(), approved_disposable_vm=False,
                    observed_broker_uid=40001, observed_broker_gid=40003)
            relay.assert_not_called()

    def test_failed_relay_is_never_retried_and_consumes_session(self):
        def fail(session, *args, **kwargs):
            raise provisioning.ProvisioningError("denied")
        with patch.object(bridge.provisioning, "relay_pinned_once",side_effect=fail) as relay:
            with self.assertRaises(bridge.BridgeError):
                bridge.bridge_inspect_only_once(
                    policy(), StubSocket(), StubSocket(),
                    pinned_hashes=pins(), approved_disposable_vm=True,
                    observed_broker_uid=40001, observed_broker_gid=40003)
            self.assertEqual(1, relay.call_count)

    def test_diagnostic_bridge_output_cannot_claim_release_or_publication(self):
        fixed=bridge.expected_upstream_denial_for_contract()
        with patch.object(bridge.provisioning, "relay_pinned_once",return_value=fixed) as relay:
            response=bridge.bridge_inspect_only_once(
                policy(),StubSocket(),StubSocket(),pinned_hashes=pins(),
                approved_disposable_vm=True,
                observed_broker_uid=40001,observed_broker_gid=40003)
            self.assertEqual("denied",response["state"])
            self.assertFalse(response["nativeSaveProven"])
            self.assertFalse(response["documentVerified"])
            self.assertFalse(response["saveVerified"])
            self.assertFalse(response["releaseReady"])
            self.assertFalse(response["workerIsolationAttested"])
            self.assertEqual(1,relay.call_count)

    def test_unexpected_permission_key_in_relay_result_is_rejected(self):
        fake=bridge.expected_upstream_denial_for_contract()
        fake["publishToWorm"]=True
        with patch.object(bridge.provisioning,"relay_pinned_once",return_value=fake):
            with self.assertRaises(bridge.BridgeError):
                bridge.bridge_inspect_only_once(
                    policy(),StubSocket(),StubSocket(),pinned_hashes=pins(),
                    approved_disposable_vm=True,
                    observed_broker_uid=40001,observed_broker_gid=40003)

    def test_listener_adapter_is_explicit_and_never_used_by_default_main(self):
        import native_saveas_broker_listener as listener
        c=policy()
        # The public normal listener still invokes its old deny-only path.
        with patch.object(bridge,"bridge_inspect_only_once",return_value={
            "state":"denied","releaseReady":False}) as run:
            result=listener.bridge_accepted_diagnostic_only(
                StubSocket(),StubSocket(),c,pinned_hashes=pins(),
                approved_disposable_vm=True,
                observed_broker_uid=40001,observed_broker_gid=40003)
            self.assertEqual("denied",result["state"])
            run.assert_called_once()

    def test_forged_positive_relay_result_always_refuses(self):
        fake=bridge.expected_upstream_denial_for_contract()
        fake["releaseReady"]=True
        with patch.object(bridge.provisioning,"relay_pinned_once",return_value=fake):
            with self.assertRaises(bridge.BridgeError):
                bridge.bridge_inspect_only_once(
                    policy(),StubSocket(),StubSocket(),pinned_hashes=pins(),
                    approved_disposable_vm=True,observed_broker_uid=40001,
                    observed_broker_gid=40003)

if __name__ == "__main__":
    unittest.main()
