"""GD31J27p: deferred post-UID AF_UNIX dialer contract, no VM or service start.

All crafted JSON and stat metadata are synthetic. Unit tests do not launch
systemd, change UIDs, bind a real socket, create corporate files, or publish.
"""
import json
import os
from pathlib import Path
import stat
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_saveas_worker_dialer as dialer


def policy():
    return {
        "schema": dialer.CONFIG_SCHEMA,
        "workerUid": 40002,
        "workerGid": 40003,
        "brokerUid": 40001,
        "ipcGid": 40003,
        "socketPath": dialer.FIXED_SOCKET,
        "mode": "diagnostic-only",
        "laboratoryOnly": True,
        "publishEnabled": False,
    }


def raw(config=None):
    return (json.dumps(config if config is not None else policy(),
                       separators=(",", ":")) + "\n").encode("ascii")


def st_file(*, uid=0, mode=0o644, size=250, nlink=1):
    return os.stat_result((stat.S_IFREG | mode, 1, 1, nlink,
                           uid, 0, size, 0, 0, 0))


def st_dir(*, uid=40001, gid=40003, mode=0o750):
    return os.stat_result((stat.S_IFDIR | mode, 1, 1, 2,
                           uid, gid, 4096, 0, 0, 0))


def st_sock(*, uid=40001, gid=40003, mode=0o660):
    return os.stat_result((stat.S_IFSOCK | mode, 1, 1, 1,
                           uid, gid, 0, 0, 0, 0))


class WorkerDialerTests(unittest.TestCase):
    def test_strict_policy_is_diagnostic_only(self):
        cfg = dialer.parse_static_policy(raw())
        self.assertEqual(dialer.FIXED_SOCKET, cfg["socketPath"])
        self.assertFalse(cfg["publishEnabled"])
        self.assertEqual("diagnostic-only", cfg["mode"])
        dialer.validate_policy_file_meta(st_file())
        dialer.validate_endpoint_meta(st_dir(), st_sock(), cfg)

    def test_injection_duplicate_keys_and_privilege_relaxation_fail(self):
        cases = [
            raw({**policy(), "mode": "save"}),
            raw({**policy(), "publishEnabled": True}),
            raw({**policy(), "workerUid": 0}),
            raw({**policy(), "brokerUid": 40002}),
            raw({**policy(), "workerGid": 0}),
            raw({**policy(), "ipcGid": 0}),
            raw({**policy(), "socketPath": "/tmp/worker.sock"}),
            raw({**policy(), "unexpected": "trusted"}),
            raw().replace(b'"workerUid":40002', b'"workerUid":40002,"workerUid":0'),
            raw()[:-1],
            raw() + raw(),
            b'x' * (dialer.MAX_POLICY_BYTES + 1),
            b'{"mode":NaN}\n',
        ]
        for data in cases:
            with self.subTest(data=data[:45]), self.assertRaises(dialer.DialerError):
                dialer.parse_static_policy(data)

    def test_policy_file_requires_single_link_root_owned_no_shared_write(self):
        for meta in (
            st_file(uid=40001), st_file(mode=0o664),
            st_file(mode=0o666), st_file(nlink=2),
            st_file(size=dialer.MAX_POLICY_BYTES+1),
            st_dir(uid=0, gid=0),
        ):
            with self.subTest(meta=meta), self.assertRaises(dialer.DialerError):
                dialer.validate_policy_file_meta(meta)

    def test_socket_and_parent_must_belong_to_broker_and_ipc_group(self):
        cfg = policy()
        variants = (
            (st_dir(uid=40002), st_sock()),
            (st_dir(mode=0o770), st_sock()),
            (st_dir(gid=40002), st_sock()),
            (st_dir(), st_sock(uid=40002)),
            (st_dir(), st_sock(mode=0o666)),
            (st_dir(), st_sock(gid=40002)),
            (st_dir(), st_file(uid=40001)),
        )
        for parent, socket_meta in variants:
            with self.subTest(parent=parent, socket_meta=socket_meta):
                with self.assertRaises(dialer.DialerError):
                    dialer.validate_endpoint_meta(parent, socket_meta, cfg)

    def test_identity_and_extra_fd_must_be_preverified(self):
        self.assertRaises(
            dialer.DialerError, dialer.validate_worker_identity, policy(),
            current_uid=40001, current_gid=40003)
        dialer.validate_worker_identity(policy(), current_uid=40002, current_gid=40003)
        with mock.patch.object(dialer.os, "getuid", return_value=40002), \
             mock.patch.object(dialer.os, "geteuid", return_value=40002), \
             mock.patch.object(dialer.os, "getgid", return_value=40003), \
             mock.patch.object(dialer.os, "getegid", return_value=40003):
            self.assertTrue(dialer.worker_identity_matches(policy()))

    def test_dialer_never_promotes_approval(self):
        result = dialer.denial()
        self.assertEqual("denied", result["state"])
        for key in ("releaseReady", "nativePublicationAuthorized",
                    "approvedForProduction", "writePermissionGranted",
                    "nativeSaveProven", "documentVerified", "saveVerified",
                    "launchAuthorized", "brokerHandshakeAttested"):
            self.assertFalse(result[key])


if __name__ == "__main__":
    unittest.main()
