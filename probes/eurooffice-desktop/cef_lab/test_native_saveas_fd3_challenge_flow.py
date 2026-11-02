"""GD31J27y deferred tests: explicit-only two-sided challenge, deny-only.

All sockets, process references and worker calls are mock-only. No launch,
production SaveAs, SCM_RIGHTS, root UID or real process is touched.
"""
from pathlib import Path
import sys
import unittest
from unittest import mock

sys.path.insert(0,str(Path(__file__).resolve().parent))
import native_saveas_fd3_challenge_flow as flow
import native_saveas_fd3_challenge as proof
import native_saveas_fd_broker as broker
import native_saveas_socket_peerpidfd as peer


def diagnostic():
    return {"state":"requires-independent-review",
            "workerFd3HandshakeObserved":True,
            "fd3PairedAttested":False,"workerExecutableAttested":False,
            "nativePublicationAuthorized":False,"releaseReady":False}


def denial():
    return {"state":"denied","nativeSaveProven":False,
            "writePermissionGranted":False,"nativePublicationAuthorized":False,
            "approvedForProduction":False,"releaseReady":False}


class FakeGuard:
    def __init__(self,order):
        self.order=order;self.closed=False
    def assert_live(self):
        self.order.append("live")
        return False
    def close(self):
        self.order.append("close");self.closed=True

class FlowTests(unittest.TestCase):
    def test_broker_challenge_before_handoff_and_pidfd_held_until_complete(self):
        order=[]
        guard=FakeGuard(order)
        session=broker.BrokerSession()
        def challenge(*a,**kw):
            order.append("challenge");return diagnostic()
        def handoff(*a,**kw):
            order.append("handoff");session.consumed=True;return denial()
        with mock.patch.object(flow.peer,"pin_socket_peer",return_value=guard) as pin,\
             mock.patch.object(flow.proof,"challenge_worker_fd3_once",
                               side_effect=challenge) as ch,\
             mock.patch.object(flow.broker,"handoff_after_kernel_review",
                               side_effect=handoff) as transport:
            result=flow.lab_broker_fd3_then_denied(
                session,object(),b"synthetic",[],broker_uid=40001,
                expected_worker_uid=40002,expected_worker_pid=33001,
                expected_worker_start_ticks=123456,
                approved_disposable_vm=True,approved_fixture_hashes={})
        self.assertEqual(["live","challenge","live","handoff","live","close"],order)
        pin.assert_called_once();ch.assert_called_once();transport.assert_called_once()
        self.assertTrue(session.consumed)
        self.assertTrue(guard.closed)
        self.assertIs(result["releaseReady"],False)

    def test_broker_wrong_response_or_exit_consumes_no_replay(self):
        for failure in (proof.Fd3ChallengeError("bad nonce"),peer.PeerPidfdError("exit")):
            session=broker.BrokerSession()
            guard=FakeGuard([])
            with self.subTest(failure=failure),\
                 mock.patch.object(flow.peer,"pin_socket_peer",return_value=guard),\
                 mock.patch.object(flow.proof,"challenge_worker_fd3_once",
                                   side_effect=failure),\
                 mock.patch.object(flow.broker,"handoff_after_kernel_review") as send:
                with self.assertRaises(flow.FlowError):
                    flow.lab_broker_fd3_then_denied(
                        session,object(),b"x",[],broker_uid=40001,
                        expected_worker_uid=40002,expected_worker_pid=33001,
                        expected_worker_start_ticks=123456,
                        approved_disposable_vm=True,approved_fixture_hashes={})
                send.assert_not_called()
                self.assertTrue(session.consumed)
                self.assertTrue(guard.closed)

    def test_worker_challenge_then_original_deny_only_receiver(self):
        events=[]
        with mock.patch.object(flow.proof,"respond_fd3_once",
                               side_effect=lambda *a,**kw:(events.append("proof"),
                                                                diagnostic())[1]),\
             mock.patch.object(flow.worker,"serve_one",
                               side_effect=lambda sock:(events.append("worker"),2)[1]):
            code=flow.lab_worker_fd3_then_denied(
                object(),expected_broker_pid=22001,expected_broker_uid=40001)
        self.assertEqual(["proof","worker"],events)
        self.assertEqual(2,code)

    def test_worker_error_never_runs_original_receiver(self):
        with mock.patch.object(flow.proof,"respond_fd3_once",
                               side_effect=proof.Fd3ChallengeError("FD3 wrong")),\
             mock.patch.object(flow.worker,"serve_one") as receiver:
            self.assertEqual(2,flow.lab_worker_fd3_then_denied(
                object(),expected_broker_pid=22001,expected_broker_uid=40001))
            receiver.assert_not_called()

if __name__=="__main__":
    unittest.main()
