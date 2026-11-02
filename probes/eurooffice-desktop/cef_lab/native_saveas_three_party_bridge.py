#!/usr/bin/env python3
"""GD31J27s: internal THREE-PARTY deny-only broker orchestration.

Requires two already connected OS AF_UNIX SOCK_SEQPACKET sockets:
root supervisor -> broker, and worker -> broker (post UID switch).
The external root supervisor, worker UID provisioning, socket ownership
and mount policies are NOT implemented or attested here. No listeners,
subprocesses, filesystem writes, SaveAs, or user-document APIs.

This is a bridge for an offline disposable Linux VM acceptance experiment
only. No call from the installed broker/worker CLI or Qt/CEF host.
"""
from __future__ import annotations

import socket
import sys

import native_saveas_fd_broker as broker
import native_saveas_fd_provisioning as provisioning
import native_saveas_fd_worker as worker

POLICY_SCHEMA="getdoc-gd31j27s-three-party-inspect-only-bridge-lab/1"
RESULT_SCHEMA="getdoc-gd31j27s-three-party-denied-result-lab/1"

class BridgeError(ValueError):
    pass

def parse_static_policy(data):
    expected={
        "schema","mode","laboratoryOnly","workerUid","brokerUid",
        "brokerGid","supervisorUid","expectedSupervisorPid",
        "expectedWorkerPid","expectedWorkerStartTicks","allowPublication",
    }
    if type(data) is not dict or set(data) != expected:
        raise BridgeError("Unexpected three-party broker policy")
    nums=("workerUid","brokerUid","brokerGid",
          "expectedSupervisorPid","expectedWorkerPid","expectedWorkerStartTicks")
    if (data["schema"] != POLICY_SCHEMA or data["mode"] != "inspect_only" or
            data["laboratoryOnly"] is not True or
            data["allowPublication"] is not False or
            type(data["supervisorUid"]) is not int or data["supervisorUid"] != 0 or
            any(type(data[k]) is not int or data[k] <= 0 for k in nums) or
            data["workerUid"] == data["brokerUid"] or
            data["expectedSupervisorPid"] == data["expectedWorkerPid"]):
        raise BridgeError("Privilege or publication policy refused")
    return data


def _exact_denial(result):
    if (type(result) is not dict or result.get("state") != "denied" or
            any(result.get(name) is not False for name in (
                "trustedSupervisorAttested", "nativePublicationAuthorized",
                "writePermissionGranted", "releaseReady",
                "approvedForProduction"))):
        raise BridgeError("Unexpected privileged relay status")


def bridge_inspect_only_once(policy, supervisor_socket, worker_socket, *,
                             pinned_hashes, approved_disposable_vm,
                             observed_broker_uid, observed_broker_gid):
    """Connect existing ports at code level, not from production CLI.

    Verifies caller's separately observed broker UID/GID; the underlying
    provisioning function separately checks actual os.geteuid(), SO_PEERCRED,
    worker PID/starttime and passes exactly 3 SCM_RIGHTS once. The caller
    assertion is NOT an independent sandbox attestation.
    """
    p=parse_static_policy(policy)
    if (not sys.platform.startswith("linux") or
            approved_disposable_vm is not True or
            type(observed_broker_uid) is not int or
            type(observed_broker_gid) is not int or
            observed_broker_uid != p["brokerUid"] or
            observed_broker_gid != p["brokerGid"] or
            type(pinned_hashes) is not dict or
            set(pinned_hashes) != set(worker.FIXTURES) or
            any(type(x) is not str or not worker.SHA.fullmatch(x) or x == "0"*64
                for x in pinned_hashes.values())):
        raise BridgeError("Three-party offline identities or fixture pins invalid")
    for sock in (supervisor_socket,worker_socket):
        if (sock.family != socket.AF_UNIX or
                sock.type & 0xF != socket.SOCK_SEQPACKET):
            raise BridgeError("Three-party sockets must be Unix seqpacket")
    session=broker.BrokerSession()
    try:
        result=provisioning.relay_pinned_once(
            session,supervisor_socket,worker_socket,
            trusted_supervisor_uid=p["supervisorUid"],
            expected_supervisor_pid=p["expectedSupervisorPid"],
            worker_uid=p["workerUid"],broker_uid=p["brokerUid"],
            expected_worker_pid=p["expectedWorkerPid"],
            expected_worker_start_ticks=p["expectedWorkerStartTicks"],
            approved_disposable_vm=approved_disposable_vm,
            independent_fixture_hashes=pinned_hashes)
        _exact_denial(result)
    except (provisioning.ProvisioningError,broker.BrokerError,BridgeError,
            OSError,ValueError) as exc:
        raise BridgeError("Three-party broker relay denied") from exc
    finally:
        session.consumed=True
    return {
        "schema":RESULT_SCHEMA,"state":"denied","laboratoryOnly":True,
        "workerIsolationAttested":False,
        "trustedSupervisorAttested":False,
        "nativePublicationAuthorized":False,
        "nativeSaveProven":False,"documentVerified":False,"saveVerified":False,
        "writePermissionGranted":False,"releaseReady":False,
        "approvedForProduction":False,
    }
