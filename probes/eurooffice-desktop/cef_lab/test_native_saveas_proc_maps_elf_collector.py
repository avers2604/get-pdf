"""GD31J27ad deferred test-first contracts: preopened offline kernel bytes.

No real Astra VM, systemd, root process, file open, tests or user data.
All fixtures are synthetic. Successful observations NEVER attest OS origin.
"""
import os
import struct
import sys
import unittest
from pathlib import Path
from unittest.mock import patch, MagicMock

sys.path.insert(0,str(Path(__file__).resolve().parent))
import native_saveas_proc_maps_elf_collector as collector

EXE="/usr/bin/python3.11"
LIB="/usr/lib/x86_64-linux-gnu/libc.so.6"

def maps(elf=LIB,extra=""):
    return (
        "00400000-00410000 r-xp 00000000 08:01 101 "+EXE+"\n"
        "7f100000-7f102000 r-xp 00000000 08:01 202 "+elf+"\n"
        "7f102000-7f103000 rw-p 00001000 08:01 202 "+elf+"\n"
        "7ff10000-7ff11000 rw-p 00000000 00:00 0 [stack]\n"
        +extra
    ).encode("ascii")

def elf64(needed=("libc.so.6",)):
    strings=b"\0"+b"".join(s.encode()+b"\0" for s in needed)
    offsets=[]
    cursor=1
    for name in needed:
        offsets.append(cursor)
        cursor+=len(name.encode())+1
    if len(strings)>256:
        raise AssertionError("fixture too long")
    binary=bytearray(1024)
    binary[:16]=b"\x7fELF"+bytes([2,1,1,0])+bytes(8)
    struct.pack_into("<HHIQQQIHHHHHH",binary,16,
                     3,62,1,0,64,0,0,64,56,2,0,0,0)
    # PT_LOAD maps virtual .strtab to file offset 0x300.
    struct.pack_into("<IIQQQQQQ",binary,64,
                     1,4,0,0x400000,0,1024,1024,0x1000)
    entries=[(5,0x400300),(10,len(strings))]
    entries.extend((1,offset) for offset in offsets)
    entries.append((0,0))
    struct.pack_into("<IIQQQQQQ",binary,120,
                     2,4,0x200,0x400200,0,len(entries)*16,len(entries)*16,8)
    for index,(tag,val) in enumerate(entries):
        struct.pack_into("<QQ",binary,0x200+index*16,tag,val)
    binary[0x300:0x300+len(strings)]=strings
    return bytes(binary)


class KernelMapsTests(unittest.TestCase):
    def test_realistic_maps_finite_exact_soname_only_denial(self):
        report=collector.parse_pinned_maps(
            maps(),executable=EXE,allowed_elf={LIB})
        self.assertEqual((LIB,),report)
        self.assertFalse(collector.denial()["osEvidenceAttested"])
        self.assertFalse(collector.denial()["releaseReady"])

    def test_unexpected_file_backed_mapping_deleted_and_extra_lib_refused(self):
        for body in (
            maps(elf="/tmp/inject.so"),
            maps(extra="7f200000-7f210000 r-xp 00000000 08:01 333 /tmp/evil.bin\n"),
            maps(elf=LIB+" (deleted)"),
            maps(extra="7f200000-7f210000 r-xp 00000000 08:01 333 /memfd:secret\n"),
            maps(extra="broken maps line\n"),
            maps(extra="7f200000-7f210000 r-xp 00000000 08:01 333 [anon:evil]\n"),
            maps()[:-1],
        ):
            with self.subTest(content=body[-50:]),self.assertRaises(collector.CollectorError):
                collector.parse_pinned_maps(body,executable=EXE,allowed_elf={LIB})

    def test_missing_library_mismatched_pid_or_empty_maps_refused(self):
        no_lib=("00400000-00410000 r-xp 00000000 08:01 101 "+EXE+"\n").encode()
        for body in (b"",no_lib):
            with self.subTest(body=body),self.assertRaises(collector.CollectorError):
                collector.parse_pinned_maps(body,executable=EXE,allowed_elf={LIB})
        with self.assertRaises(collector.CollectorError):
            collector.parse_pinned_maps(maps(),executable="/tmp/python",allowed_elf={LIB})


class ElfNeededTests(unittest.TestCase):
    def test_elf64_little_endian_needed_is_extracted_from_binary_not_shell(self):
        result=collector.parse_elf64_needed(elf64(),expected_machine=62)
        self.assertEqual(("libc.so.6",),result)
        self.assertFalse(collector.denial()["runtimeLibrariesAttested"])

    def test_malformed_unmapped_or_truncated_elf_denied(self):
        for image in (
            b"not-elf",
            elf64()[:32],
            bytes([0])+elf64()[1:],
            elf64().replace(b"\x7fELF\x02\x01",b"\x7fELF\x01\x01",1),
            elf64().replace(b"\x7fELF\x02\x01",b"\x7fELF\x02\x02",1),
        ):
            with self.subTest(image=image[:12]),self.assertRaises(collector.CollectorError):
                collector.parse_elf64_needed(image,expected_machine=62)
        with self.assertRaises(collector.CollectorError):
            collector.parse_elf64_needed(elf64(),expected_machine=183)
        damaged=bytearray(elf64())
        struct.pack_into("<Q",damaged,0x200+16,0xFFFFFFFFFFFFFFFF) # bad DT_STRSZ
        with self.assertRaises(collector.CollectorError):
            collector.parse_elf64_needed(bytes(damaged),expected_machine=62)

    def test_missing_needed_entry_fails_against_catalog(self):
        expected={LIB:("libc.so.6",)}
        observed={LIB:()}
        with self.assertRaises(collector.CollectorError):
            collector.compare_elf_needed(expected,observed)
        self.assertFalse(collector.compare_elf_needed(expected,expected))


class FixedPreopenedFds(unittest.TestCase):
    def test_exact_two_preopened_proc_fds_and_one_elf_fd_are_review_only(self):
        owner={"broker":32001,"worker":33001}
        opened={"broker":15,"worker":16}
        elf={LIB:17}
        facts={15:maps(),16:maps(),17:elf64(())}
        sizes=[]
        def read_bounded(fd,cap):
            sizes.append((fd,cap))
            return facts[fd]
        with patch.object(collector,"_read_bounded_preopened_fd",
                          side_effect=read_bounded) as reader:
            report=collector.capture_preopened_once(
                proc_map_fds=opened,elf_fds=elf,expected_pids=owner,
                executable_by_role={"broker":EXE,"worker":EXE},
                expected_elf_paths={LIB},expected_needed={LIB:()},
                approved_disposable_vm=True,expected_machine=62)
            self.assertEqual(3,reader.call_count)
        self.assertTrue(report["kernelMapFactsObserved"])
        self.assertTrue(report["elfNeededFactsObserved"])
        self.assertFalse(report["osEvidenceAttested"])
        self.assertFalse(report["releaseReady"])

    def test_invalid_fd_or_vm_gate_cannot_read_any_kernel_data(self):
        common=dict(proc_map_fds={"broker":15,"worker":16},elf_fds={LIB:17},
            expected_pids={"broker":32001,"worker":33001},
            executable_by_role={"broker":EXE,"worker":EXE},
            expected_elf_paths={LIB},expected_needed={LIB:()},
            approved_disposable_vm=True,expected_machine=62)
        for changes in (
            {"approved_disposable_vm":False},
            {"proc_map_fds":{"broker":15,"worker":15}},
            {"proc_map_fds":{"broker":0,"worker":16}},
            {"elf_fds":{LIB:15}},
            {"elf_fds":{"/tmp/evil.so":17}},
            {"expected_pids":{"broker":32001,"worker":32001}},
            {"expected_pids":{"broker":True,"worker":33001}},
        ):
            props={**common,**changes}
            with self.subTest(changes=changes),patch.object(
                    collector,"_read_bounded_preopened_fd") as reader:
                with self.assertRaises(collector.CollectorError):
                    collector.capture_preopened_once(**props)
                reader.assert_not_called()

    def test_preopened_fd_not_regular_or_not_readonly_refused_before_read(self):
        import stat
        for mode,access in ((stat.S_IFSOCK,os.O_RDONLY),
                            (stat.S_IFREG,os.O_WRONLY),
                            (stat.S_IFREG,os.O_RDWR)):
            fake=type("Meta",(),{"st_mode":mode})()
            with self.subTest(mode=mode,access=access),\
                 patch.object(collector.os,"fstat",return_value=fake),\
                 patch.object(collector.fcntl,"fcntl",return_value=access),\
                 patch.object(collector.os,"read") as reader:
                with self.assertRaises(collector.CollectorError):
                    collector._read_bounded_preopened_fd(17,20)
                reader.assert_not_called()

    def test_bounded_reader_does_not_open_or_close_owned_fd(self):
        import stat
        fake=type("Meta",(),{"st_mode":stat.S_IFREG})()
        with patch.object(collector.os,"read",
                          side_effect=[b"a"*5,b""]) as read,\
             patch.object(collector.os,"fstat",return_value=fake),\
             patch.object(collector.fcntl,"fcntl",return_value=os.O_RDONLY),\
             patch.object(collector.os,"open",create=True) as opened,\
             patch.object(collector.os,"close") as closed:
            self.assertEqual(b"a"*5,collector._read_bounded_preopened_fd(17,10))
            read.assert_called()
            opened.assert_not_called()
            closed.assert_not_called()
        with patch.object(collector.os,"read",return_value=b"x"*11),\
             patch.object(collector.os,"fstat",return_value=fake),\
             patch.object(collector.fcntl,"fcntl",return_value=os.O_RDONLY):
            with self.assertRaises(collector.CollectorError):
                collector._read_bounded_preopened_fd(17,10)

if __name__=="__main__":
    unittest.main()
