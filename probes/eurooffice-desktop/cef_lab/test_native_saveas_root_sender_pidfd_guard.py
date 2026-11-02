"""GD31J27v deferred sender integration tests: no real root or pidfd.

All socket, /proc and fd actions are stubbed. Does not run commands, services,
systemd or actual root privileges.
"""
import os
from pathlib import Path
import socket
import struct
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0,str(Path(__file__).resolve().parent))
import native_saveas_root_supervisor as root
import native_saveas_pidfd_provenance as pidfd


def config():
    return {
        "schema":root.MANIFEST_SCHEMA,"workerUid":40002,"brokerUid":40001,
        "brokerGid":40003,"expectedBrokerPid":32001,
        "expectedBrokerStartTicks":123456,
        "fixtureName":"gd31j6_writer.docx",
        "fixtureDigests":{"gd31j6_writer.docx":"a"*64,
                          "gd31j6_calc.xlsx":"b"*64,
                          "gd31j6_impress.pptx":"c"*64},
        "sessionId":"102030405060708090a0b0c0d0e0f010",
        "tabId":"f010e0d0c0b0a0908070605040302010",
        "operation":"inspect_only","publishEnabled":False,
        "laboratoryOnly":True,
    }


def observation():
    return {
        "state":"requires-independent-review","kernelFactsObserved":True,
        "rootSandboxAttested":False,"seccompAllowlistAttested":False,
        "mountRestrictionAttested":False,"nativePublicationAuthorized":False,
        "launchAuthorized":False,"releaseReady":False,
        "approvedForProduction":False,"writePermissionGranted":False,
    }


class FakeSock:
    family=socket.AF_UNIX
    type=socket.SOCK_SEQPACKET
    def __init__(self):
        self.sends=0
    def getsockopt(self,*args):
        return struct.pack("3i",32001,40001,40003)
    def settimeout(self,value):
        pass
    def sendmsg(self,body,control):
        self.sends+=1
        return len(body[0])


class Guard:
    def __init__(self,fail_check=None):
        self.checks=0
        self.closed=0
        self.fail_check=fail_check
    def assert_live(self):
        self.checks+=1
        if self.checks==self.fail_check:
            raise pidfd.PidfdError("reaped process")
        return False
    def close(self):
        self.closed+=1


class RootSenderGuardTests(unittest.TestCase):
    def _send(self,guard):
        sock=FakeSock()
        session=root.RootSendOnce()
        with patch.object(root.os,"getuid",return_value=0),\
             patch.object(root.os,"geteuid",return_value=0),\
             patch.object(root.audit,"inspect_current_root",return_value=observation()),\
             patch.object(root.audit,"require_broker_lifetime",return_value=False),\
             patch.object(root.pidfd,"pin_broker_process",return_value=guard) as pin,\
             patch.object(root,"open_verified_handles",return_value=[70,71,72]) as opened,\
             patch.object(root.os,"close") as close:
            try:
                result=session.send(sock,config(),gate=root.VM_TEST_ONLY_GATE)
            except root.SupervisorError:
                result=None
        return result,sock,guard,opened,pin,close,session

    def test_pinned_pidfd_must_remain_alive_before_and_after_one_fd_send(self):
        result,sock,guard,opened,pin,closed,session=self._send(Guard())
        self.assertEqual("sent-unattested",result["state"])
        self.assertEqual(1,sock.sends)
        self.assertGreaterEqual(guard.checks,2)
        self.assertEqual(1,guard.closed)
        pin.assert_called_once_with(32001,123456)
        self.assertTrue(session.consumed)
        self.assertFalse(result["releaseReady"])

    def test_broker_exit_before_fd_open_fails_closed(self):
        result,sock,guard,opened,pin,closed,session=self._send(Guard(fail_check=1))
        self.assertIsNone(result)
        opened.assert_not_called()
        self.assertEqual(0,sock.sends)
        self.assertEqual(1,guard.closed)
        self.assertTrue(session.consumed)

    def test_broker_exit_after_send_is_not_retried(self):
        result,sock,guard,opened,pin,closed,session=self._send(Guard(fail_check=2))
        self.assertIsNone(result)
        self.assertEqual(1,sock.sends)
        self.assertEqual(1,guard.closed)
        self.assertTrue(session.consumed)


if __name__=="__main__":
    unittest.main()
