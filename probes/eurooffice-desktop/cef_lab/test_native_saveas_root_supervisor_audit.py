"""GD31J27u deferred RED tests: broker PID birth identity and root sandbox evidence.

Synthetic text only: no real root/service/VM/SCM_RIGHTS execution.
"""
import json
import os
import pathlib
import socket
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0,str(pathlib.Path(__file__).resolve().parent))
import native_saveas_root_supervisor_audit as audit
import native_saveas_root_supervisor as root

ROOT_STATUS = """Name:\tgetdoc-lab-supervisor
Uid:\t0\t0\t0\t0
Gid:\t0\t0\t0\t0
NoNewPrivs:\t1
Seccomp:\t2
Seccomp_filters:\t1
TracerPid:\t0
CapInh:\t0000000000000000
CapPrm:\t0000000000000004
CapEff:\t0000000000000004
CapBnd:\t0000000000000004
CapAmb:\t0000000000000000
"""
MOUNTS = """23 1 0:21 / / rw,relatime - tmpfs tmpfs rw
24 23 0:22 / /run rw,nosuid,nodev - tmpfs tmpfs rw
25 23 8:2 / /usr ro,relatime - ext4 /dev/vda2 ro
"""


class RootSandboxEvidenceTests(unittest.TestCase):
    def inspect(self, status=ROOT_STATUS, mounts=MOUNTS, **extra):
        kw={"root_status_text":status,"mountinfo_text":mounts,
            "root_mount_ns_inode":10001,"host_mount_ns_inode":10002,
            "root_net_ns_inode":11001,"host_net_ns_inode":11002}
        kw.update(extra)
        return audit.inspect_root_observation(**kw)

    def test_positive_observations_always_remain_nonattested(self):
        report=self.inspect()
        self.assertEqual("requires-independent-review",report["state"])
        self.assertTrue(report["kernelFactsObserved"])
        self.assertTrue(report["dacReadSearchObserved"])
        for key in ("rootSandboxAttested","manifestTrustedByOs",
                    "corporateFilesAbsentAttested","seccompAllowlistAttested",
                    "mountRestrictionAttested","brokerProcessPinnedAttested",
                    "nativePublicationAuthorized","launchAuthorized",
                    "writePermissionGranted","releaseReady",
                    "approvedForProduction"):
            self.assertIs(report[key],False)

    def test_dac_bypass_sysadmin_wrong_uid_and_tracer_are_refused(self):
        violations=(
            ROOT_STATUS.replace("CapEff:\t0000000000000004",
                                "CapEff:\t0000000000000006"),
            ROOT_STATUS.replace("CapBnd:\t0000000000000004",
                                "CapBnd:\t0000000000200004"),
            ROOT_STATUS.replace("Uid:\t0\t0\t0\t0",
                                "Uid:\t0\t0\t1000\t0"),
            ROOT_STATUS.replace("NoNewPrivs:\t1","NoNewPrivs:\t0"),
            ROOT_STATUS.replace("Seccomp:\t2","Seccomp:\t0"),
            ROOT_STATUS.replace("TracerPid:\t0","TracerPid:\t1"),
            ROOT_STATUS + "CapEff:\t0000000000000004\n",
        )
        for item in violations:
            with self.subTest(item=item[-56:]),self.assertRaises(audit.AuditError):
                self.inspect(status=item)

    def test_external_mount_or_same_namespace_is_refused(self):
        with self.assertRaises(audit.AuditError):
            self.inspect(mounts=MOUNTS + "26 23 0:3 / /mnt/x rw - cifs //corp rw\n")
        for change in ({"root_mount_ns_inode":10002},
                       {"root_net_ns_inode":11002},
                       {"root_mount_ns_inode":0}):
            with self.subTest(change=change),self.assertRaises(audit.AuditError):
                self.inspect(**change)

    def test_independent_peer_start_time_pinned_and_positive_never_approved(self):
        with patch.object(audit,"read_broker_start_ticks",return_value=123456):
            self.assertFalse(audit.require_broker_lifetime(
                broker_pid=32001,expected_start_ticks=123456))
            with self.assertRaises(audit.AuditError):
                audit.require_broker_lifetime(
                    broker_pid=32001,expected_start_ticks=123457)
        with self.assertRaises(audit.AuditError):
            audit.require_broker_lifetime(broker_pid=0,expected_start_ticks=123456)


class ThreePartyPolicyAlignmentTests(unittest.TestCase):
    def policies(self):
        import native_saveas_worker_dialer as dialer
        import native_saveas_three_party_bridge as bridge
        root_cfg={
            "schema":"getdoc-gd31j27t-root-supervisor-synthetic-manifest-lab/1",
            "workerUid":40002,"brokerUid":40001,"brokerGid":40003,
            "expectedBrokerPid":32001,"expectedBrokerStartTicks":123456,
            "fixtureName":"gd31j6_writer.docx",
            "fixtureDigests":{"gd31j6_writer.docx":"a"*64,
                             "gd31j6_calc.xlsx":"b"*64,
                             "gd31j6_impress.pptx":"c"*64},
            "sessionId":"102030405060708090a0b0c0d0e0f010",
            "tabId":"f010e0d0c0b0a0908070605040302010",
            "operation":"inspect_only","publishEnabled":False,
            "laboratoryOnly":True,
        }
        dial={
            "schema":dialer.CONFIG_SCHEMA,
            "workerUid":40002,"workerGid":40003,
            "brokerUid":40001,"ipcGid":40003,
            "socketPath":dialer.FIXED_SOCKET,"mode":"diagnostic-only",
            "laboratoryOnly":True,"publishEnabled":False,
        }
        br={
            "schema":bridge.POLICY_SCHEMA,"mode":"inspect_only",
            "laboratoryOnly":True,"workerUid":40002,"brokerUid":40001,
            "brokerGid":40003,"supervisorUid":0,
            "expectedSupervisorPid":33001,"expectedWorkerPid":33002,
            "expectedWorkerStartTicks":123457,"allowPublication":False,
        }
        return root_cfg,dial,br

    def test_matching_policies_still_have_no_os_attestation(self):
        r,d,b=self.policies()
        result=audit.inspect_three_party_alignment(r,d,b)
        self.assertEqual("requires-independent-review",result["state"])
        self.assertFalse(result["rootSandboxAttested"])
        self.assertFalse(result["launchAuthorized"])
        self.assertFalse(result["releaseReady"])

    def test_uid_gid_and_unsupported_flags_mismatch_fail_closed(self):
        for index,field,value in (
            (0,"workerUid",40005),(0,"brokerGid",40004),
            (1,"ipcGid",40004),(1,"workerUid",40006),
            (2,"brokerUid",40002),(2,"supervisorUid",1),
            (2,"allowPublication",True),(0,"publishEnabled",True),
        ):
            with self.subTest(index=index,field=field):
                parts=list(self.policies())
                parts[index]={**parts[index],field:value}
                with self.assertRaises(audit.AuditError):
                    audit.inspect_three_party_alignment(*parts)


def synthetic_root_observed():
    return {"state":"requires-independent-review","kernelFactsObserved":True,
            "rootSandboxAttested":False,"seccompAllowlistAttested":False,
            "mountRestrictionAttested":False,"nativePublicationAuthorized":False,
            "launchAuthorized":False,"releaseReady":False,
            "approvedForProduction":False,"writePermissionGranted":False}


class SenderBirthtimeGates(unittest.TestCase):
    def config(self):
        return {"schema":root.MANIFEST_SCHEMA,"workerUid":40002,
                "brokerUid":40001,"brokerGid":40003,
                "expectedBrokerPid":32001,"expectedBrokerStartTicks":123456,
                "fixtureName":"gd31j6_writer.docx",
                "fixtureDigests":{"gd31j6_writer.docx":"a"*64,
                                  "gd31j6_calc.xlsx":"b"*64,
                                  "gd31j6_impress.pptx":"c"*64},
                "sessionId":"102030405060708090a0b0c0d0e0f010",
                "tabId":"f010e0d0c0b0a0908070605040302010",
                "operation":"inspect_only","publishEnabled":False,
                "laboratoryOnly":True}

    def test_missing_or_invalid_start_ticks_denied_in_root_manifest(self):
        cfg=self.config()
        self.assertEqual(123456,root.validate_manifest(cfg)["expectedBrokerStartTicks"])
        for bad in (0,-1,True,"123456",None):
            x={**cfg,"expectedBrokerStartTicks":bad}
            with self.subTest(bad=bad),self.assertRaises(root.SupervisorError):
                root.validate_manifest(x)
        x=dict(cfg);del x["expectedBrokerStartTicks"]
        with self.assertRaises(root.SupervisorError):
            root.validate_manifest(x)

    def test_stale_broker_process_stops_before_fd_open(self):
        from test_native_saveas_root_supervisor import FakeBroker
        sender=root.RootSendOnce()
        with patch.object(root.os,"getuid",return_value=0),\
             patch.object(root.os,"geteuid",return_value=0),\
             patch.object(root.audit,"inspect_current_root",
                          return_value=synthetic_root_observed()),\
             patch.object(root.audit,"require_broker_lifetime",
                          side_effect=audit.AuditError("stale pid")),\
             patch.object(root,"open_verified_handles") as open_fds:
            with self.assertRaises(root.SupervisorError):
                sender.send(FakeBroker(),self.config(),
                            gate=root.VM_TEST_ONLY_GATE)
            open_fds.assert_not_called()
        self.assertTrue(sender.consumed)

    def test_pid_switch_after_send_produces_uncertain_denial(self):
        from test_native_saveas_root_supervisor import FakeBroker
        broker=FakeBroker()
        sender=root.RootSendOnce()
        with patch.object(root.os,"getuid",return_value=0),\
             patch.object(root.os,"geteuid",return_value=0),\
             patch.object(root.audit,"inspect_current_root",
                          return_value=synthetic_root_observed()),\
             patch.object(root.audit,"require_broker_lifetime",
                          side_effect=[False,audit.AuditError("recycled pid")]),\
             patch.object(root,"open_verified_handles",return_value=[11,12,13]),\
             patch.object(root.os,"close"):
            with self.assertRaises(root.SupervisorError):
                sender.send(broker,self.config(),gate=root.VM_TEST_ONLY_GATE)
        self.assertEqual(1,len(broker.sent))
        self.assertTrue(sender.consumed)


if __name__=="__main__":
    unittest.main()
