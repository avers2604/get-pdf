#!/usr/bin/env python3
"""GD31J27y: UNWIRED explicit FD3 nonce challenge + old denial-only IPC.

Neither worker.main(), worker_dialer.main(), broker_listener.main() nor CEF
calls these functions. The broker holds a socket-peer pidfd throughout the
challenge and underlying one-shot SCM_RIGHTS/denied exchange. A caller MUST
provide independently pinned OS process identities and a disposable offline VM.

Observations are not executable provenance, approved Astra MAC/BPF, endpoint
kernel-pair attestation or permission to publish. Fail closed, never replay.
"""
from __future__ import annotations

import native_saveas_fd3_challenge as proof
import native_saveas_socket_peerpidfd as peer
import native_saveas_fd_broker as broker
import native_saveas_fd_worker as worker

class FlowError(ValueError):
    pass


def _exact_denial(result):
    if (type(result) is not dict or result.get("state")!="denied" or
            any(result.get(field) is not False for field in (
                "nativePublicationAuthorized","nativeSaveProven",
                "writePermissionGranted","approvedForProduction",
                "releaseReady"))):
        raise FlowError("Worker transport returned unexpected authority")


def lab_broker_fd3_then_denied(session,sock,packet,handles,*,
                               broker_uid,expected_worker_uid,
                               expected_worker_pid,
                               expected_worker_start_ticks,
                               approved_disposable_vm,
                               approved_fixture_hashes):
    """EXPLICIT-only optional route, not the broker's default main().

    Only an approved disposable lab caller can opt in. The underlying
    broker.handoff_after_kernel_review still enforces the exact worker
    denial schema and retains its own socket-peer/worker pidfd guards.
    """
    if (not isinstance(session,broker.BrokerSession) or session.consumed or
            approved_disposable_vm is not True):
        raise FlowError("Offline worker nonce session not approved")
    pinned=None
    try:
        pinned=peer.pin_socket_peer(
            sock,expected_worker_pid=expected_worker_pid,
            expected_worker_uid=expected_worker_uid,
            broker_uid=broker_uid,
            expected_worker_start_ticks=expected_worker_start_ticks)
        pinned.assert_live()
        result=proof.challenge_worker_fd3_once(
            sock,expected_worker_pid=expected_worker_pid,
            expected_worker_uid=expected_worker_uid,
            broker_uid=broker_uid)
        if (type(result) is not dict or
                result.get("state")!="requires-independent-review" or
                result.get("workerFd3HandshakeObserved") is not True or
                any(result.get(flag) is not False for flag in (
                    "fd3PairedAttested","workerExecutableAttested",
                    "nativePublicationAuthorized","writePermissionGranted",
                    "releaseReady"))):
            raise FlowError("Worker FD3 nonce observation refused")
        pinned.assert_live()
        response=broker.handoff_after_kernel_review(
            session,sock,packet,handles,
            broker_uid=broker_uid,
            expected_worker_uid=expected_worker_uid,
            expected_worker_pid=expected_worker_pid,
            expected_worker_start_ticks=expected_worker_start_ticks,
            approved_disposable_vm=approved_disposable_vm,
            approved_fixture_hashes=approved_fixture_hashes)
        pinned.assert_live()
        _exact_denial(response)
        return response
    except (FlowError,peer.PeerPidfdError,proof.Fd3ChallengeError,
            broker.BrokerError,OSError,ValueError) as exc:
        session.consumed=True
        raise FlowError("One-shot FD3 evidence or denied worker relay refused") from exc
    finally:
        session.consumed=True
        if pinned is not None:
            try:
                pinned.close()
            except (peer.PeerPidfdError,OSError) as exc:
                raise FlowError("Worker socket peer guard close failed") from exc


def lab_worker_fd3_then_denied(sock,*,expected_broker_pid,expected_broker_uid):
    """EXPLICIT-only alternate synthetic worker route; always exit status 2.

    The stock worker.main and worker_dialer remain unchanged and NEVER call
    this function. The original serve_one only accepts inspect_only and
    cannot publish a file.
    """
    try:
        proof.respond_fd3_once(
            sock,expected_broker_pid=expected_broker_pid,
            expected_broker_uid=expected_broker_uid)
        return worker.serve_one(sock)
    except (proof.Fd3ChallengeError,worker.WorkerError,OSError,ValueError):
        return 2
