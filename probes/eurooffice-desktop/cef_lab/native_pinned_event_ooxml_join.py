#!/usr/bin/env python3
"""GD-31J.27c: READ-ONLY pinned v2 events + independent OOXML/canary join.

No process launching, no native stdout provenance claim, no document writes.
Failure diagnostics are intentionally NOT independently verified OOXML.
"""
from __future__ import annotations

import sys
sys.dont_write_bytecode = True

import argparse
import io
import json

import native_ooxml_join as old_join
import native_pinned_saveas_event_v2 as event_v2
import synthetic_roundtrip as ooxml

SCHEMA = "getdoc-native-pinned-v2-ooxml-joined-lab/1"


class V2JoinError(ValueError):
    """Strict synthetic-only read-only refusal."""


def verify(staging, fixture, session, tab, raw):
    try:
        diagnostic = event_v2.parse(raw, session, tab, fixture)
    except event_v2.PinnedEventError as exc:
        raise V2JoinError("Native pinned v2 event receipt rejected") from exc
    if diagnostic["diagnosticState"] != "synthetic_candidate_untrusted":
        raise V2JoinError("No eligible unique ordered synthetic native candidate")
    # v2 parser already rejected duplicate fields, unknown fields and forged
    # authority, so the inner object is safe to reserialize for strict v1
    # candidate verification. No downgrade of protocols occurs.
    native = json.loads(raw)["receipt"]
    try:
        independent = old_join.correlate(
            staging, fixture, session, tab,
            json.dumps(native, separators=(",", ":")))
    except (old_join.JoinError, ooxml.LabError, OSError) as exc:
        raise V2JoinError("Independent OOXML/canary/digest check refused candidate") from exc
    if (independent["outputSha256"] != diagnostic["outputSha256"] or
            not independent["syntheticCanaryVerified"] or
            not independent["outputDigestCorrelated"]):
        raise V2JoinError("Pinned event v2 differs from independently verified OOXML")
    return {
        "schema": SCHEMA,
        "sessionId": session,
        "tabId": tab,
        "fixtureName": fixture,
        "failureCode": diagnostic["failureCode"],
        "nativeEventOrderSelfReported": True,
        "outputSha256": independent["outputSha256"],
        "sourceSha256": independent["sourceSha256"],
        "independentOoxmlCorrelated": True,
        "nativeSaveProven": False,
        "documentVerified": False,
        "saveVerified": False,
        "approvedForProduction": False,
        "laboratoryOnly": True,
    }


def from_stream(staging, fixture, session, tab, stream):
    data = stream.read(event_v2.MAX_BYTES + 2)
    if (type(data) is not bytes or not 3 <= len(data) <= event_v2.MAX_BYTES + 1 or
            data.count(b"\n") != 1 or not data.endswith(b"\n") or
            b"\r" in data):
        raise V2JoinError("Expected one bounded native v2 JSON line")
    try:
        payload = data[:-1].decode("ascii", "strict")
    except UnicodeError as exc:
        raise V2JoinError("Native v2 stdout is not ASCII") from exc
    return verify(staging, fixture, session, tab, payload)


def main(argv=None):
    parser = argparse.ArgumentParser(description="READ-ONLY synthetic pinned v2 SaveAs + OOXML")
    parser.add_argument("--staging", required=True)
    parser.add_argument("--fixture", required=True, choices=ooxml.FIXTURES)
    parser.add_argument("--session", required=True)
    parser.add_argument("--tab", required=True)
    args = parser.parse_args(argv)
    try:
        report = from_stream(
            args.staging, args.fixture, args.session, args.tab, sys.stdin.buffer)
    except V2JoinError as exc:
        print("GD31J27C_REFUSED: " + str(exc), file=sys.stderr)
        return 2
    print(json.dumps(report, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
