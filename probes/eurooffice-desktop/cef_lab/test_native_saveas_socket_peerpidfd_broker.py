"""GD31J27x: deferred broker mock tests for socket-bound SO_PEERPIDFD.

No real socket, SCM_RIGHTS, root, procfs or publication. Source-only stage.
"""
from pathlib import Path
import sys
import unittest
from unittest import mock
sys.path.insert(0,str(Path(__file__).resolve().parent))
import native_saveas_fd_broker as broker
import native_saveas_supervisor_handoff as observer
import native_saveas_socket_peerpidfd as socket_peer


def valid_observation():
    obj=observer.default_handoff_gate()
    obj.update({"observedWorkerFds":4,"observedFd3Socket":True})
    return obj

def denied_result():
    return {
        "schema":broker.BROKER_SCHEMA,"state":"denied",
        "workerPreflightAttested":False,"nativeSaveProven":False,
        "documentVerified":False,"saveVerified":False,
        "releaseReady":False,"approvedForProduction":False,
        "writePermissionGranted":False,"laboratoryOnly":True,
    }

class TraceGuard:
    def __init__(self,name,events,fail_after=None):
        self.name=name
        self.events=events
        self.n=0
        self.fail_after=fail_after
    def assert_live(self):
        self.n+=1
        self.events.append(self.name+".live")
        if self.n==self.fail_after:
            raise socket_peer.PeerPidfdError("mock exit")
        return False
    def close(self):
        self.events.append(self.name+".close")

class FakeSession(broker.BrokerSession):
    def __init__(self,events):
        super().__init__()
        self.events=events
        self.handoffs=0
    def handoff(self,*a,**kw):
        self.events.append("send-and-denied-reply")
        self.handoffs+=1
        self.consumed=True
        return denied_result()


class SocketBoundBrokerTests(unittest.TestCase):
    def send(self,*,socket_guard,worker_guard=None,observation=None):
        events=socket_guard.events
        worker_guard=worker_guard or TraceGuard("worker",events)
        session=FakeSession(events)
        def get_socket(*a,**kw):
            events.append("socket.peerpidfd")
            return socket_guard
        def get_worker(*a,**kw):
            events.append("worker.pidfd")
            return worker_guard
        def read_observation(*a,**kw):
            events.append("procfs.FD3")
            return valid_observation() if observation is None else observation
        with mock.patch.object(broker.socket_peer,"pin_socket_peer",side_effect=get_socket) as bound,\
             mock.patch.object(broker.pidfd,"pin_worker_process",side_effect=get_worker) as opened,\
             mock.patch.object(broker.supervisor_handoff,"read_actual_handoff",
                               side_effect=read_observation):
            result=broker.handoff_after_kernel_review(
                session,object(),b"synthetic",[],
                broker_uid=40001,expected_worker_uid=40002,
                expected_worker_pid=33001,expected_worker_start_ticks=123456,
                approved_disposable_vm=True,approved_fixture_hashes={})
        bound.assert_called_once()
        opened.assert_called_once_with(33001,123456)
        return events,session,result

    def test_socket_pidfd_kept_across_worker_review_and_denied_reply(self):
        events=[]
        ev,session,result=self.send(socket_guard=TraceGuard("socket",events))
        self.assertLess(ev.index("socket.peerpidfd"),ev.index("procfs.FD3"))
        self.assertLess(ev.index("procfs.FD3"),ev.index("send-and-denied-reply"))
        self.assertLess(ev.index("send-and-denied-reply"),ev.index("socket.close"))
        self.assertIn("socket.live",ev)
        self.assertEqual(1,ev.count("socket.close"))
        self.assertEqual(1,ev.count("worker.close"))
        self.assertEqual("denied",result["state"])
        self.assertIs(result["nativePublicationAuthorized"],False)
        self.assertTrue(session.consumed)

    def test_unavailable_socket_peer_pidfd_denies_before_procfs_or_send(self):
        events=[]
        session=FakeSession(events)
        with mock.patch.object(broker.socket_peer,"pin_socket_peer",
                               side_effect=socket_peer.PeerPidfdError("ENOPROTOOPT")),\
             mock.patch.object(broker.supervisor_handoff,
                               "read_actual_handoff") as check:
            with self.assertRaises(broker.BrokerError):
                broker.handoff_after_kernel_review(
                    session,object(),b"x",[],broker_uid=40001,
                    expected_worker_uid=40002,expected_worker_pid=33001,
                    expected_worker_start_ticks=123456,
                    approved_disposable_vm=True,approved_fixture_hashes={})
            check.assert_not_called()
        self.assertTrue(session.consumed)
        self.assertEqual(0,session.handoffs)

    def test_socket_peer_exits_after_send_result_unknown_no_retry(self):
        events=[]
        guard=TraceGuard("socket",events,fail_after=2)
        session=FakeSession(events)
        with mock.patch.object(broker.socket_peer,"pin_socket_peer",return_value=guard),\
             mock.patch.object(broker.pidfd,"pin_worker_process",
                               return_value=TraceGuard("worker",events)),\
             mock.patch.object(broker.supervisor_handoff,"read_actual_handoff",
                               return_value=valid_observation()):
            with self.assertRaises(broker.BrokerError):
                broker.handoff_after_kernel_review(
                    session,object(),b"x",[],broker_uid=40001,
                    expected_worker_uid=40002,expected_worker_pid=33001,
                    expected_worker_start_ticks=123456,
                    approved_disposable_vm=True,approved_fixture_hashes={})
        self.assertEqual(1,session.handoffs)
        self.assertTrue(session.consumed)
        self.assertEqual(1,events.count("socket.close"))
        self.assertEqual(1,events.count("worker.close"))


if __name__=="__main__":
    unittest.main()
