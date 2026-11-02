#!/usr/bin/env python3
"""GD31J27ag: one-shot strict synthetic three-party denial transcript reader.

This is NOT a process launcher, not an actual E2E and not an IPC client.
It only checks a bounded canonical synthetic operator transcript. No shell,
network, file paths, systemd, subprocess, PIDs, root rights, Linux kernel
calls or user documents. All observations are UNTRUSTED claims; a malicious
author can fabricate every event, including the final denial.

The externally pinned run ID and fixture hash do not establish the origin
of the trace. Success never authorizes publication, a service start, or
native SaveAs, and never marks any security evidence as attested.
"""
from __future__ import annotations

import hashlib
import hmac
import json
import re

TRACE_SCHEMA="getdoc-gd31j27ag-synthetic-three-party-denial-transcript/1"
RESULT_SCHEMA="getdoc-gd31j27ag-synthetic-transcript-review-deny-only/1"
MAX_TRACE_BYTES=8192
RUN_ID=re.compile(r"[0-9a-f]{32}\Z",re.ASCII)
SHA64=re.compile(r"[0-9a-f]{64}\Z",re.ASCII)
TOP_KEYS=frozenset(("schema","source","mode","runId","fixtureSha256",
                    "documentAccessCount","status","events"))
EVENT_KEYS=frozenset(("step","role","event","result"))

# Transcript order only. These labels do not prove socket delivery,
# SCM_RIGHTS correctness, executable origin or a real denial from worker.
EXPECTED_EVENTS=(
    ("supervisor","vm-preflight","observed"),
    ("supervisor","root-manifest-review","observed"),
    ("supervisor","synthetic-fds-offered","observed"),
    ("broker","synthetic-fds-observed","observed"),
    ("broker","nonce-issued","observed"),
    ("worker","fd3-nonce-received","observed"),
    ("worker","inspect-only-refusal","denied"),
    ("broker","refusal-received","denied"),
    ("supervisor","no-write-cleanup-observed","observed"),
)


class TraceError(ValueError):
    """Sanitized error; do not echo the transcript, fixture or run ID."""


def _unique_pairs(pairs):
    fields={}
    for key,val in pairs:
        if key in fields:
            raise TraceError("Duplicate synthetic transcript JSON key")
        fields[key]=val
    return fields


def _canonical(data):
    try:
        return (json.dumps(data,sort_keys=True,separators=(",",":"),
                           ensure_ascii=True,allow_nan=False)+"\n").encode("ascii")
    except (TypeError,ValueError,UnicodeError,RecursionError) as exc:
        raise TraceError("Noncanonical synthetic transcript") from exc


def denial():
    return {
        "schema":RESULT_SCHEMA,
        "state":"requires-independent-review",
        "traceOrderObserved":False,
        "noDocumentAccessClaimObserved":False,
        "osEvidenceAttested":False,
        "transportAttested":False,
        "workerExecutableAttested":False,
        "trustedVmIsolationAttested":False,
        "trustedSupervisorAttested":False,
        "nativePublicationAuthorized":False,
        "writePermissionGranted":False,
        "releaseReady":False,
        "approvedForProduction":False,
        "laboratoryOnly":True,
    }


def _validate_external_pins(expected_run_id,expected_fixture_sha256):
    if (type(expected_run_id) is not str or
            RUN_ID.fullmatch(expected_run_id) is None or
            expected_run_id=="0"*32 or
            type(expected_fixture_sha256) is not str or
            SHA64.fullmatch(expected_fixture_sha256) is None or
            expected_fixture_sha256=="0"*64):
        raise TraceError("No independent synthetic transcript run/fixture pins")


def _parse(raw):
    if (type(raw) is not bytes or
            not 4<=len(raw)<=MAX_TRACE_BYTES or
            raw.count(b"\n")!=1 or not raw.endswith(b"\n") or
            b"\r" in raw):
        raise TraceError("Invalid bounded synthetic transcript")
    try:
        obj=json.loads(raw[:-1].decode("ascii"),
                       object_pairs_hook=_unique_pairs,
                       parse_constant=lambda _: (_ for _ in ()).throw(
                           TraceError("Invalid JSON numeric constant")))
    except (ValueError,UnicodeError,RecursionError) as exc:
        if isinstance(exc,TraceError):
            raise
        raise TraceError("Invalid synthetic transcript JSON") from exc
    if _canonical(obj)!=raw:
        raise TraceError("Synthetic transcript is not exact canonical JSON")
    if (type(obj) is not dict or set(obj)!=TOP_KEYS or
            obj["schema"]!=TRACE_SCHEMA or
            obj["source"]!="synthetic-operator-replay-untrusted" or
            obj["mode"]!="inspect_only" or
            obj["status"]!="denied" or
            type(obj["documentAccessCount"]) is not int or
            obj["documentAccessCount"]!=0 or
            type(obj["runId"]) is not str or
            RUN_ID.fullmatch(obj["runId"]) is None or
            type(obj["fixtureSha256"]) is not str or
            SHA64.fullmatch(obj["fixtureSha256"]) is None):
        raise TraceError("Untrusted synthetic transcript publication or document access")
    events=obj["events"]
    if type(events) is not list or len(events)!=len(EXPECTED_EVENTS):
        raise TraceError("Missing or additional synthetic process events")
    for index,(actual,expected) in enumerate(zip(events,EXPECTED_EVENTS)):
        if (type(actual) is not dict or set(actual)!=EVENT_KEYS or
                type(actual["step"]) is not int or actual["step"]!=index+1 or
                (actual["role"],actual["event"],actual["result"])!=expected):
            raise TraceError("Out-of-order or non-denial three-party event")
    return obj


class OneShotTraceReview:
    """One synthetic trace per instance. Failed attempts consume the gate."""
    def __init__(self):
        self.consumed=False

    def inspect(self,raw,*,expected_run_id,expected_fixture_sha256):
        if self.consumed:
            raise TraceError("One-shot transcript already consumed")
        self.consumed=True
        _validate_external_pins(expected_run_id,expected_fixture_sha256)
        received=_parse(raw)
        if (not hmac.compare_digest(received["runId"],expected_run_id) or
                not hmac.compare_digest(
                    received["fixtureSha256"],expected_fixture_sha256)):
            raise TraceError("Synthetic transcript run or fixture pin mismatch")
        return {
            **denial(),
            "traceOrderObserved":True,
            "noDocumentAccessClaimObserved":True,
        }
