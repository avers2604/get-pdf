"""GD31J27p: deferred real after-UID listener source & credential contracts.

No systemd calls, real socket bind, UID switch, real test run or SaveAs.
"""
import sys
from pathlib import Path
import unittest
from unittest import mock
sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_saveas_broker_listener as listener
import native_saveas_worker_dialer as dialer


def cfg():
    return dialer.parse_static_policy(
        (Path(__file__).resolve().parent/
         "getdoc-gd31j27p-worker-policy.example.json").read_bytes())


class BrokerListenerTests(unittest.TestCase):
    def test_correct_separate_uid_and_group_required(self):
        listener.validate_broker_identity(cfg(),
                                          current_uid=40001,
                                          current_gid=40003)
        for uid,gid in ((0,40003),(40002,40003),(40001,0),
                        (40001,40002)):
            with self.subTest(uid=uid,gid=gid):
                with self.assertRaises(listener.ListenerError):
                    listener.validate_broker_identity(
                        cfg(),current_uid=uid,current_gid=gid)

    def test_worker_connection_must_show_post_switch_peer_credentials(self):
        listener.validate_worker_peer(
            pid=22001, uid=40002, gid=40003, policy=cfg())
        for pid,uid,gid in ((0,40002,40003),(22001,0,40003),
                            (22001,40001,40003),(22001,40002,0),
                            (22001,40002,40001)):
            with self.subTest(pid=pid,uid=uid,gid=gid):
                with self.assertRaises(listener.ListenerError):
                    listener.validate_worker_peer(
                        pid=pid, uid=uid, gid=gid, policy=cfg())

    def test_deny_only_response_never_entitles_socket_to_publish(self):
        result=listener.denial()
        self.assertEqual("denied",result["state"])
        self.assertEqual("diagnostic-only",result["mode"])
        for key in ("nativePublicationAuthorized","nativeSaveProven",
                    "approvedForProduction","releaseReady","writePermissionGranted",
                    "launchAuthorized","kernelIsolationAttested"):
            self.assertIs(result[key],False)

if __name__=="__main__":
    unittest.main()
