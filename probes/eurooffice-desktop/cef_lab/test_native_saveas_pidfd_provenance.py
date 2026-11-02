"""GD31J27v deferred pidfd tests: synthetic process identities only.

No pidfd_open on real processes, no signals, no ptrace, no root service,
no actual UNIX FD transport and no SaveAs. Do not run outside offline lab.
"""
import fcntl
import pathlib
import select
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0,str(pathlib.Path(__file__).resolve().parent))
import native_saveas_pidfd_provenance as pidfd


class FakePoll:
    def __init__(self, events=None):
        self.events = [] if events is None else events
        self.registrations = []
        self.waits = []
    def register(self, fd, events):
        self.registrations.append((fd,events))
    def poll(self, timeout):
        self.waits.append(timeout)
        return self.events


class PidfdProvenanceTests(unittest.TestCase):
    def test_opened_pinned_pidfd_nonblocking_and_closed(self):
        poll=FakePoll()
        with patch.object(pidfd.os,"pidfd_open",return_value=55) as opener,\
             patch.object(pidfd.os,"close") as close,\
             patch.object(pidfd.fcntl,"fcntl",return_value=fcntl.FD_CLOEXEC),\
             patch.object(pidfd.audit,"read_broker_start_ticks",
                          return_value=123456) as reader,\
             patch.object(pidfd.select,"poll",return_value=poll):
            with pidfd.pin_broker_process(32001,123456) as handle:
                self.assertIs(handle.assert_live(),False)
                self.assertEqual(55,handle.fileno())
                self.assertFalse(handle.processIdentityAttested)
                self.assertFalse(handle.launchAuthorized)
                self.assertFalse(handle.releaseReady)
            opener.assert_called_once_with(32001,0)
            close.assert_called_once_with(55)
            self.assertGreaterEqual(reader.call_count,3)
        self.assertTrue(all(v==0 for v in poll.waits))
        self.assertTrue(all(fd==55 for fd,_ in poll.registrations))

    def test_process_died_or_reaped_is_a_hard_refusal(self):
        for event in (select.POLLIN,select.POLLHUP,select.POLLERR,
                      select.POLLNVAL):
            with self.subTest(event=event),\
                 patch.object(pidfd.os,"pidfd_open",return_value=55),\
                 patch.object(pidfd.os,"close") as close,\
                 patch.object(pidfd.fcntl,"fcntl",return_value=fcntl.FD_CLOEXEC),\
                 patch.object(pidfd.audit,"read_broker_start_ticks",
                              return_value=123456),\
                 patch.object(pidfd.select,"poll",
                              return_value=FakePoll(events=[(55,event)])):
                with self.assertRaises(pidfd.PidfdError):
                    with pidfd.pin_broker_process(32001,123456):
                        pass
                close.assert_called_once_with(55)

    def test_pid_recycled_before_or_after_pidfd_open(self):
        for readings in ([123457],[123456,123457]):
            with self.subTest(readings=readings),\
                 patch.object(pidfd.os,"pidfd_open",return_value=55),\
                 patch.object(pidfd.os,"close") as close,\
                 patch.object(pidfd.fcntl,"fcntl",return_value=fcntl.FD_CLOEXEC),\
                 patch.object(pidfd.audit,"read_broker_start_ticks",
                              side_effect=readings),\
                 patch.object(pidfd.select,"poll",return_value=FakePoll()):
                with self.assertRaises(pidfd.PidfdError):
                    pidfd.pin_broker_process(32001,123456)
                if len(readings)>1:
                    close.assert_called_once_with(55)

    def test_missing_pidfd_or_non_cloexec_never_uses_proc_open_fallback(self):
        with patch.object(pidfd.os,"pidfd_open",
                          side_effect=OSError(38,"no pidfd support")),\
             patch.object(pidfd.audit,"read_broker_start_ticks",
                          return_value=123456),\
             patch.object(pidfd.os,"open") as open_fallback:
            with self.assertRaises(pidfd.PidfdError):
                pidfd.pin_broker_process(32001,123456)
            open_fallback.assert_not_called()
        with patch.object(pidfd.os,"pidfd_open",return_value=55),\
             patch.object(pidfd.os,"close") as close,\
             patch.object(pidfd.fcntl,"fcntl",return_value=0),\
             patch.object(pidfd.audit,"read_broker_start_ticks",
                          return_value=123456):
            with self.assertRaises(pidfd.PidfdError):
                pidfd.pin_broker_process(32001,123456)
            close.assert_called_once_with(55)

    def test_invalid_identity_is_rejected_without_open(self):
        for pid,ticks in ((0,123),(-1,123),(True,123),
                          (32001,0),(32001,True),(32001,"123")):
            with self.subTest(pid=pid,ticks=ticks),\
                 patch.object(pidfd.os,"pidfd_open") as opener:
                with self.assertRaises(pidfd.PidfdError):
                    pidfd.pin_broker_process(pid,ticks)
                opener.assert_not_called()


if __name__=="__main__":
    unittest.main()
