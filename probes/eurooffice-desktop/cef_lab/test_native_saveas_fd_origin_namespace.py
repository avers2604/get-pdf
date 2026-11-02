"""GD31J27ae deferred mock-only tests for independent FD-origin observations.

No real /proc, UID0, pidfd, systemd, ELF files, IPC or user documents.
Nothing here represents OS or production attestation.
"""
import hashlib
import os
import stat
import sys
from pathlib import Path
from types import SimpleNamespace
from unittest import TestCase, main
from unittest.mock import patch

sys.path.insert(0,str(Path(__file__).resolve().parent))
import native_saveas_fd_origin_namespace as origin
import native_saveas_proc_maps_elf_collector as maps

LIB="/usr/lib/x86_64-linux-gnu/libc.so.6"
EXE="/usr/bin/python3.11"
SHA=hashlib.sha256(b"synthetic fixed ELF").hexdigest()


def inputs():
    return dict(
        proc_map_fds={"broker":14,"worker":15},
        elf_fds={LIB:16},
        expected_pids={"broker":32001,"worker":33001},
        expected_start_ticks={"broker":123456,"worker":123457},
        expected_namespaces={
            "broker":{"mnt":101,"net":102,"pid":103},
            "worker":{"mnt":201,"net":202,"pid":203},
        },
        expected_elf_sha256={LIB:SHA},
        executable_by_role={"broker":EXE,"worker":EXE},
        expected_elf_paths={LIB},
        expected_needed={LIB:()},
        expected_machine=62,
        approved_disposable_vm=True,
    )


class Guard:
    def __init__(self,role,events):
        self.role=role
        self.events=events
    def assert_live(self):
        self.events.append(self.role+".live")
        return False
    def close(self):
        self.events.append(self.role+".close")


class OriginTests(TestCase):
    def test_fd_origin_and_namespace_hold_pins_across_parser_then_close(self):
        events=[]
        args=inputs()
        targets={
            "/proc/self/fd/14":"/proc/32001/maps",
            "/proc/self/fd/15":"/proc/33001/maps",
            "/proc/self/fd/16":LIB,
        }
        ns={}
        for role,pid in args["expected_pids"].items():
            for name,number in args["expected_namespaces"][role].items():
                ns[f"/proc/{pid}/ns/{name}"]=SimpleNamespace(st_ino=number)
        def get_guard(role):
            def pin(*a,**kw):
                events.append(role+".pin")
                return Guard(role,events)
            return pin
        def get_target(path):
            events.append("fd.path")
            return targets[path]
        def get_ns(path):
            events.append("ns.stat")
            return ns[path]
        def hash_elf(*a,**kw):
            events.append("elf.hash")
            return SHA
        def capture(**kw):
            events.append("capture")
            return {**maps.denial(),"kernelMapFactsObserved":True,
                    "elfNeededFactsObserved":True}
        with patch.object(origin.os,"geteuid",return_value=0),\
             patch.object(origin.sys,"platform","linux"),\
             patch.object(origin.pidfd,"pin_broker_process",
                          side_effect=get_guard("broker")) as bp,\
             patch.object(origin.pidfd,"pin_worker_process",
                          side_effect=get_guard("worker")) as wp,\
             patch.object(origin.os,"readlink",side_effect=get_target),\
             patch.object(origin.os,"stat",side_effect=get_ns),\
             patch.object(origin,"_hash_root_elf_fd",side_effect=hash_elf),\
             patch.object(origin.maps,"capture_preopened_once",
                          side_effect=capture) as called:
            report=origin.review_fd_origins_once(**args)
        bp.assert_called_once_with(32001,123456)
        wp.assert_called_once_with(33001,123457)
        called.assert_called_once()
        self.assertLess(events.index("broker.pin"),events.index("capture"))
        self.assertLess(events.index("worker.pin"),events.index("capture"))
        self.assertLess(events.index("capture"),events.index("broker.close"))
        self.assertEqual(1,events.count("worker.close"))
        self.assertEqual(1,events.count("broker.close"))
        self.assertTrue(report["kernelFdPathObserved"])
        self.assertTrue(report["kernelNamespaceFactsObserved"])
        self.assertTrue(report["elfRootOwnedShaObserved"])
        for name in ("fdOriginAttested","osEvidenceAttested",
                     "nativePublicationAuthorized","releaseReady"):
            self.assertIs(report[name],False)

    def test_bad_role_pin_alias_fd_or_vm_gate_refuses_before_os_access(self):
        for change in (
            {"approved_disposable_vm":False},
            {"proc_map_fds":{"broker":14,"worker":14}},
            {"elf_fds":{LIB:14}},
            {"expected_pids":{"broker":32001,"worker":32001}},
            {"expected_start_ticks":{"broker":123456,"worker":True}},
            {"expected_namespaces":{"broker":{"mnt":101,"net":102,"pid":103}}},
            {"expected_elf_sha256":{LIB:"0"*64}},
            {"elf_fds":{"/tmp/evil.so":16}},
            {"expected_elf_paths":{"/tmp/evil.so"}},
        ):
            args={**inputs(),**change}
            with self.subTest(change=change),\
                 patch.object(origin.os,"geteuid") as uid,\
                 patch.object(origin.pidfd,"pin_broker_process") as pin:
                with self.assertRaises(origin.OriginError):
                    origin.review_fd_origins_once(**args)
                uid.assert_not_called()
                pin.assert_not_called()

    def test_nonroot_refused_before_pidfd(self):
        with patch.object(origin.os,"geteuid",return_value=40001),\
             patch.object(origin.pidfd,"pin_broker_process") as pin:
            with self.assertRaises(origin.OriginError):
                origin.review_fd_origins_once(**inputs())
            pin.assert_not_called()

    def test_fake_proc_maps_fd_path_refused_before_parser_and_guards_closed(self):
        args=inputs()
        def fake_target(path):
            if path.endswith("/14"):
                return "/tmp/fake-maps"
            if path.endswith("/15"):
                return "/proc/33001/maps"
            return LIB
        events=[]
        with patch.object(origin.os,"geteuid",return_value=0),\
             patch.object(origin.pidfd,"pin_broker_process",
                          return_value=Guard("broker",events)),\
             patch.object(origin.pidfd,"pin_worker_process",
                          return_value=Guard("worker",events)),\
             patch.object(origin.os,"readlink",side_effect=fake_target),\
             patch.object(origin.maps,"capture_preopened_once") as capture:
            with self.assertRaises(origin.OriginError):
                origin.review_fd_origins_once(**args)
            capture.assert_not_called()
        self.assertEqual(1,events.count("broker.close"))
        self.assertEqual(1,events.count("worker.close"))

    def test_kernel_namespace_swap_after_capture_refused_with_no_retry(self):
        args=inputs()
        events=[]
        count={"mnt":0}
        def get_ns(path):
            inode=next(args["expected_namespaces"][role][name]
                       for role,pid in args["expected_pids"].items()
                       for name in ("mnt","net","pid")
                       if path==f"/proc/{pid}/ns/{name}")
            if path=="/proc/33001/ns/mnt":
                count["mnt"]+=1
                if count["mnt"]>=2:
                    inode=999
            return SimpleNamespace(st_ino=inode)
        def fd_target(path):
            return {"/proc/self/fd/14":"/proc/32001/maps",
                    "/proc/self/fd/15":"/proc/33001/maps",
                    "/proc/self/fd/16":LIB}[path]
        with patch.object(origin.os,"geteuid",return_value=0),\
             patch.object(origin.pidfd,"pin_broker_process",
                          return_value=Guard("broker",events)),\
             patch.object(origin.pidfd,"pin_worker_process",
                          return_value=Guard("worker",events)),\
             patch.object(origin.os,"readlink",side_effect=fd_target),\
             patch.object(origin.os,"stat",side_effect=get_ns),\
             patch.object(origin,"_hash_root_elf_fd",return_value=SHA),\
             patch.object(origin.maps,"capture_preopened_once",
                          return_value={**maps.denial(),"kernelMapFactsObserved":True,
                                        "elfNeededFactsObserved":True}) as capture:
            with self.assertRaises(origin.OriginError):
                origin.review_fd_origins_once(**args)
            capture.assert_called_once()
        self.assertEqual(1,events.count("broker.close"))
        self.assertEqual(1,events.count("worker.close"))

    def test_wrong_elf_digest_refused_before_capture_and_closes_both_pidfds(self):
        args=inputs()
        events=[]
        with patch.object(origin.os,"geteuid",return_value=0),\
             patch.object(origin.pidfd,"pin_broker_process",
                          return_value=Guard("broker",events)),\
             patch.object(origin.pidfd,"pin_worker_process",
                          return_value=Guard("worker",events)),\
             patch.object(origin.os,"readlink",side_effect=lambda p:{
                 "/proc/self/fd/14":"/proc/32001/maps",
                 "/proc/self/fd/15":"/proc/33001/maps",
                 "/proc/self/fd/16":LIB}[p]),\
             patch.object(origin.os,"stat",side_effect=lambda p:SimpleNamespace(
                 st_ino=next(args["expected_namespaces"][role][ns]
                             for role,pid in args["expected_pids"].items()
                             for ns in ("mnt","net","pid")
                             if p==f"/proc/{pid}/ns/{ns}"))),\
             patch.object(origin,"_hash_root_elf_fd",return_value="f"*64),\
             patch.object(origin.maps,"capture_preopened_once") as capture:
            with self.assertRaises(origin.OriginError):
                origin.review_fd_origins_once(**args)
            capture.assert_not_called()
        self.assertEqual(1,events.count("broker.close"))
        self.assertEqual(1,events.count("worker.close"))

    def test_readonly_root_elf_hash_requires_stable_inode_and_no_write(self):
        meta=SimpleNamespace(st_mode=stat.S_IFREG|0o644,st_uid=0,
                             st_nlink=1,st_size=len(b"synthetic fixed ELF"),
                             st_dev=1,st_ino=2,st_mtime_ns=3,st_ctime_ns=4)
        payload=b"synthetic fixed ELF"
        with patch.object(origin.os,"fstat",return_value=meta),\
             patch.object(origin.fcntl,"fcntl",return_value=os.O_RDONLY),\
             patch.object(origin.os,"pread",side_effect=lambda fd,n,off:payload[off:off+n]):
            self.assertEqual(SHA,origin._hash_root_elf_fd(16))
        for bad in (
            SimpleNamespace(**{**vars(meta),"st_uid":40002}),
            SimpleNamespace(**{**vars(meta),"st_mode":stat.S_IFLNK|0o777}),
            SimpleNamespace(**{**vars(meta),"st_mode":stat.S_IFREG|0o666}),
            SimpleNamespace(**{**vars(meta),"st_nlink":2}),
        ):
            with self.subTest(meta=vars(bad)),\
                 patch.object(origin.os,"fstat",return_value=bad),\
                 patch.object(origin.fcntl,"fcntl",return_value=os.O_RDONLY),\
                 patch.object(origin.os,"pread") as read:
                with self.assertRaises(origin.OriginError):
                    origin._hash_root_elf_fd(16)
                read.assert_not_called()

if __name__=="__main__":
    main()
