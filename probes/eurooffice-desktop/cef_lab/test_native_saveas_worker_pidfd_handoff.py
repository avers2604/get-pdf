"""GD31J27w: deferred worker pidfd guard between preflight and handoff.

All checks are mock-only. No live process, pidfd_open, SCM_RIGHTS, UID change,
root services, user documents or filesystem publication occurs here.
"""
from pathlib import Path
import sys
import unittest
from unittest import mock

sys.path.insert(0,str(Path(__file__).resolve().parent))
import native_saveas_fd_broker as broker
import native_saveas_pidfd_provenance as pidfd
import native_saveas_supervisor_handoff as observer


class FakeWorker:
    pass


def kernel_observation():
    report=observer.default_handoff_gate()
    report.update({"observedWorkerFds":4,"observedFd3Socket":True})
    return report


def expected_denial():
    return {
        "schema":broker.BROKER_SCHEMA,"state":"denied",
        "workerPreflightAttested":False,"nativeSaveProven":False,
        "documentVerified":False,"saveVerified":False,
        "releaseReady":False,"approvedForProduction":False,
        "writePermissionGranted":False,"laboratoryOnly":True,
    }


class FakeSession(broker.BrokerSession):
    def __init__(self,ordered,answer=None):
        super().__init__()
        self.ordered=ordered
        self.answer=expected_denial() if answer is None else answer
        self.handoffs=0
    def handoff(self,*args,**kwargs):
        self.ordered.append("send")
        self.handoffs+=1
        self.consumed=True
        return dict(self.answer)


class Guard:
    def __init__(self,ordered,fail_on=0):
        self.ordered=ordered
        self.fail_on=fail_on
        self.checks=0
        self.closed=0
    def assert_live(self):
        self.ordered.append("check")
        self.checks+=1
        if self.checks==self.fail_on:
            raise pidfd.PidfdError("mock worker process exited")
        return False
    def close(self):
        self.ordered.append("close")
        self.closed+=1


class WorkerPidfdBrokerTests(unittest.TestCase):
    def run_bridge(self,guard,ordered,report=None):
        session=FakeSession(ordered)
        def observed(*args,**kwargs):
            ordered.append("kernel-review")
            return kernel_observation() if report is None else report
        with mock.patch.object(broker.pidfd,"pin_worker_process",
                               return_value=guard) as pin,\
             mock.patch.object(broker.supervisor_handoff,"read_actual_handoff",
                               side_effect=observed):
            result=broker.handoff_after_kernel_review(
                session,FakeWorker(),b"synthetic-only",[],
                broker_uid=40001,expected_worker_uid=40002,
                expected_worker_pid=33001,expected_worker_start_ticks=123456,
                approved_disposable_vm=True,approved_fixture_hashes={})
            pin.assert_called_once_with(33001,123456)
        return session,result

    def test_pin_before_kernel_review_hold_across_send_close_after_reply(self):
        ordered=[]
        guard=Guard(ordered)
        session,result=self.run_bridge(guard,ordered)
        self.assertEqual(
            ["kernel-review","check","send","check","close"],
            ordered)
        self.assertEqual(2,guard.checks)
        self.assertEqual(1,guard.closed)
        self.assertTrue(session.consumed)
        self.assertEqual("denied",result["state"])
        for field in ("nativePublicationAuthorized","releaseReady",
                      "approvedForProduction","writePermissionGranted",
                      "kernelIsolationAttested","workerPreflightAttested"):
            self.assertIs(result[field],False)

    def test_worker_exit_before_fd_send_is_fail_closed_and_consumes_session(self):
        ordered=[]
        guard=Guard(ordered,fail_on=1)
        session=FakeSession(ordered)
        with mock.patch.object(broker.pidfd,"pin_worker_process",
                               return_value=guard),\
             mock.patch.object(broker.supervisor_handoff,"read_actual_handoff",
                               return_value=kernel_observation()):
            with self.assertRaises(broker.BrokerError):
                broker.handoff_after_kernel_review(
                    session,FakeWorker(),b"x",[],broker_uid=40001,
                    expected_worker_uid=40002,expected_worker_pid=33001,
                    expected_worker_start_ticks=123456,
                    approved_disposable_vm=True,approved_fixture_hashes={})
        self.assertEqual(0,session.handoffs)
        self.assertTrue(session.consumed)
        self.assertEqual(1,guard.closed)

    def test_worker_exit_after_send_has_unknown_outcome_never_retries(self):
        ordered=[]
        guard=Guard(ordered,fail_on=2)
        session=FakeSession(ordered)
        with mock.patch.object(broker.pidfd,"pin_worker_process",
                               return_value=guard),\
             mock.patch.object(broker.supervisor_handoff,"read_actual_handoff",
                               return_value=kernel_observation()):
            with self.assertRaises(broker.BrokerError):
                broker.handoff_after_kernel_review(
                    session,FakeWorker(),b"x",[],broker_uid=40001,
                    expected_worker_uid=40002,expected_worker_pid=33001,
                    expected_worker_start_ticks=123456,
                    approved_disposable_vm=True,approved_fixture_hashes={})
        self.assertEqual(1,session.handoffs)
        self.assertTrue(session.consumed)
        self.assertEqual(1,guard.closed)
        self.assertEqual(["check","send","check","close"],ordered)

    def test_pidfd_unavailable_does_not_read_worker_or_send(self):
        ordered=[]
        session=FakeSession(ordered)
        with mock.patch.object(broker.pidfd,"pin_worker_process",
                               side_effect=pidfd.PidfdError("ENOSYS")),\
             mock.patch.object(broker.supervisor_handoff,
                               "read_actual_handoff") as observed:
            with self.assertRaises(broker.BrokerError):
                broker.handoff_after_kernel_review(
                    session,FakeWorker(),b"x",[],broker_uid=40001,
                    expected_worker_uid=40002,expected_worker_pid=33001,
                    expected_worker_start_ticks=123456,
                    approved_disposable_vm=True,approved_fixture_hashes={})
            observed.assert_not_called()
        self.assertTrue(session.consumed)
        self.assertEqual(0,session.handoffs)

    def test_forged_positive_worker_response_must_be_refused(self):
        ordered=[]
        guard=Guard(ordered)
        session=FakeSession(ordered,{**expected_denial(),"releaseReady":True})
        with mock.patch.object(broker.pidfd,"pin_worker_process",
                               return_value=guard),\
             mock.patch.object(broker.supervisor_handoff,"read_actual_handoff",
                               return_value=kernel_observation()):
            with self.assertRaises(broker.BrokerError):
                broker.handoff_after_kernel_review(
                    session,FakeWorker(),b"x",[],broker_uid=40001,
                    expected_worker_uid=40002,expected_worker_pid=33001,
                    expected_worker_start_ticks=123456,
                    approved_disposable_vm=True,approved_fixture_hashes={})
        self.assertEqual(1,guard.closed)
        self.assertEqual(1,session.handoffs)


if __name__=="__main__":
    unittest.main()
