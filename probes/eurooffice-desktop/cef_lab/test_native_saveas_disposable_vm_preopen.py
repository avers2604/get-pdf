"""GD31J27af deferred mock-only fixed preopen laboratory regressions.

DO NOT run live under root or corporate filesystems. No real VMs, tests,
kernel FD reads, save operations or service management occurred in this PR.
"""
import hashlib
import os
import stat
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0,str(Path(__file__).resolve().parent))
import native_saveas_disposable_vm_preopen as vm

LIB="/usr/lib/x86_64-linux-gnu/libc.so.6"
EXE="/usr/bin/python3.11"
SHA=hashlib.sha256(b"synthetic pinned ELF").hexdigest()

def plan():
    return dict(
        expected_pids={"broker":32001,"worker":33001},
        expected_start_ticks={"broker":123456,"worker":123457},
        expected_namespaces={
            "broker":{"mnt":101,"net":102,"pid":103},
            "worker":{"mnt":201,"net":202,"pid":203},
        },
        expected_elf_sha256={LIB:SHA},
        executable_by_role={"broker":EXE,"worker":EXE},
        expected_needed={LIB:()},
        expected_machine=62,
        approved_disposable_vm=True,
        operator_ack=vm.EXPLICIT_ACK,
    )

def diagnostic():
    return {**vm.origin.denial(),"kernelFdPathObserved":True,
            "kernelNamespaceFactsObserved":True,
            "elfRootOwnedShaObserved":True}

class MockGuard:
    def __init__(self,role,events):
        self.role=role;self.events=events
    def assert_live(self):
        self.events.append(self.role+".live")
        return False
    def close(self):
        self.events.append(self.role+".close")

class PreopenTests(unittest.TestCase):
    def mock_happy(self):
        events=[]
        def proc(pid):
            events.append(f"proc.{pid}")
            return 20 if pid==32001 else 21
        def lib(path):
            events.append("elf")
            return 22
        def pin(role):
            def inner(pid,start):
                events.append(role+".pin")
                return MockGuard(role,events)
            return inner
        return events,proc,lib,pin

    def test_gate_before_uid_or_open_rejects_unsafe_parameters(self):
        for override in (
            {"operator_ack":""},
            {"approved_disposable_vm":False},
            {"expected_pids":{"broker":32001,"worker":32001}},
            {"expected_pids":{"broker":True,"worker":33001}},
            {"expected_start_ticks":{"broker":123456,"worker":True}},
            {"expected_namespaces":{"broker":{"mnt":101,"net":102,"pid":103}}},
            {"expected_elf_sha256":{LIB:"0"*64}},
            {"expected_elf_sha256":{"/tmp/evil.so":SHA}},
            {"expected_machine":True},
            {"expected_needed":{"/tmp/evil.so":()}},
        ):
            with self.subTest(override=override),\
                 patch.object(vm.os,"geteuid") as user,\
                 patch.object(vm,"_open_fixed_proc_map") as proc,\
                 patch.object(vm,"_open_fixed_elf") as elf:
                with self.assertRaises(vm.PreopenError):
                    vm.preopen_disposable_once(**{**plan(),**override})
                user.assert_not_called()
                proc.assert_not_called();elf.assert_not_called()

    def test_uid_nonroot_stops_before_pidfd_or_file_open(self):
        with patch.object(vm.os,"geteuid",return_value=40001),\
             patch.object(vm.pidfd,"pin_broker_process") as pin,\
             patch.object(vm,"_open_fixed_proc_map") as proc:
            with self.assertRaises(vm.PreopenError):
                vm.preopen_disposable_once(**plan())
            pin.assert_not_called();proc.assert_not_called()

    def test_two_pinned_processes_open_only_fixed_proc_and_elf_then_close(self):
        events,proc,elf,pin=self.mock_happy()
        def inspect(**kw):
            events.append("review")
            self.assertEqual({"broker":20,"worker":21},kw["proc_map_fds"])
            self.assertEqual({LIB:22},kw["elf_fds"])
            self.assertIs(kw["approved_disposable_vm"],True)
            return diagnostic()
        with patch.object(vm.os,"geteuid",return_value=0),\
             patch.object(vm.pidfd,"pin_broker_process",side_effect=pin("broker")),\
             patch.object(vm.pidfd,"pin_worker_process",side_effect=pin("worker")),\
             patch.object(vm,"_open_fixed_proc_map",side_effect=proc),\
             patch.object(vm,"_open_fixed_elf",side_effect=elf),\
             patch.object(vm.origin,"review_fd_origins_once",side_effect=inspect),\
             patch.object(vm.os,"close",side_effect=lambda fd:events.append(f"fd.{fd}.close")):
            report=vm.preopen_disposable_once(**plan())
        self.assertEqual(["broker.pin","worker.pin","broker.live","worker.live",
                          "proc.32001","proc.33001","elf","review",
                          "broker.live","worker.live",
                          "fd.22.close","fd.21.close","fd.20.close",
                          "worker.close","broker.close"],events)
        self.assertIs(report["fdOriginAttested"],False)
        self.assertIs(report["nativePublicationAuthorized"],False)
        self.assertIs(report["releaseReady"],False)
        self.assertTrue(report["fixedReadOnlyDescriptorsOpened"])

    def test_open_failure_closes_all_earlier_files_and_guards(self):
        events,proc,elf,pin=self.mock_happy()
        def bad_elf(path):
            raise OSError(13,"not permitted")
        with patch.object(vm.os,"geteuid",return_value=0),\
             patch.object(vm.pidfd,"pin_broker_process",side_effect=pin("broker")),\
             patch.object(vm.pidfd,"pin_worker_process",side_effect=pin("worker")),\
             patch.object(vm,"_open_fixed_proc_map",side_effect=proc),\
             patch.object(vm,"_open_fixed_elf",side_effect=bad_elf),\
             patch.object(vm.origin,"review_fd_origins_once") as inspect,\
             patch.object(vm.os,"close",side_effect=lambda fd:events.append(f"fd.{fd}.close")):
            with self.assertRaises(vm.PreopenError):
                vm.preopen_disposable_once(**plan())
            inspect.assert_not_called()
        self.assertEqual(1,events.count("fd.20.close"))
        self.assertEqual(1,events.count("fd.21.close"))
        self.assertEqual(1,events.count("worker.close"))
        self.assertEqual(1,events.count("broker.close"))

    def test_kernel_review_with_authorizing_response_is_refused(self):
        events,proc,elf,pin=self.mock_happy()
        result={**diagnostic(),"releaseReady":True}
        with patch.object(vm.os,"geteuid",return_value=0),\
             patch.object(vm.pidfd,"pin_broker_process",side_effect=pin("broker")),\
             patch.object(vm.pidfd,"pin_worker_process",side_effect=pin("worker")),\
             patch.object(vm,"_open_fixed_proc_map",side_effect=proc),\
             patch.object(vm,"_open_fixed_elf",side_effect=elf),\
             patch.object(vm.origin,"review_fd_origins_once",return_value=result),\
             patch.object(vm.os,"close",side_effect=lambda fd:events.append(f"fd.{fd}.close")):
            with self.assertRaises(vm.PreopenError):
                vm.preopen_disposable_once(**plan())
        self.assertEqual(3,sum(e.endswith(".close") for e in events if e.startswith("fd.")))

    def test_fixed_path_opener_is_readonly_nofollow_root_beneath(self):
        opened=[]
        closed=[]
        # Root-owned parent directories and regular non-writable leaf.
        def osopen(name,flags,**kwargs):
            opened.append((name,flags,kwargs))
            return 10+len(opened)
        def osstat(fd):
            idx=fd-11
            mode=stat.S_IFREG|0o644 if idx==len(opened)-1 and len(opened)==5 else stat.S_IFDIR|0o755
            return type("M",(),{"st_mode":mode,"st_uid":0,"st_nlink":1})()
        with patch.object(vm.os,"open",side_effect=osopen),\
             patch.object(vm.os,"fstat",side_effect=osstat),\
             patch.object(vm.os,"close",side_effect=lambda fd:closed.append(fd)):
            result=vm._open_fixed_elf(LIB)
        self.assertEqual(15,result)
        self.assertEqual(["/","usr","lib","x86_64-linux-gnu","libc.so.6"],
                         [entry[0] for entry in opened])
        self.assertTrue(all(flags & os.O_NOFOLLOW for _,flags,_ in opened))
        self.assertTrue(all(flags & os.O_CLOEXEC for _,flags,_ in opened))
        self.assertEqual([14,13,12,11],closed)

    def test_symlink_or_group_writable_path_fails_and_all_fds_closed(self):
        opened=[];closed=[]
        def osopen(name,flags,**kwargs):
            opened.append(name);return 30+len(opened)
        def fake_stat(fd):
            meta=dict(st_mode=stat.S_IFDIR|0o755,st_uid=0,st_nlink=1)
            if fd==32:meta["st_mode"]=stat.S_IFLNK|0o777
            return type("M",(),meta)()
        with patch.object(vm.os,"open",side_effect=osopen),\
             patch.object(vm.os,"fstat",side_effect=fake_stat),\
             patch.object(vm.os,"close",side_effect=lambda fd:closed.append(fd)):
            with self.assertRaises(vm.PreopenError):
                vm._open_fixed_elf(LIB)
        self.assertEqual([32,31],closed)

if __name__=="__main__":
    unittest.main()
