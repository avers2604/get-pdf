#!/usr/bin/env python3
"""GD-31J.27b: read-only, mutually exclusive SaveAs protocol diagnostics.

Recognizes the EXACT legacy guarded v2 receipt and the pinned native v1.
Neither is signed, neither proves OOXML, and neither grants saving authority.
This is NOT a conversion or downgrade path into native_ooxml_join.py.
"""
from __future__ import annotations

import sys
sys.dont_write_bytecode = True

import argparse
import io
import json
import re

RESULT_SCHEMA = "getdoc-native-saveas-protocol-diagnostic-lab/1"
CURRENT_SCHEMA = "getdoc-native-saveas-candidate-lab/1"
LEGACY_SCHEMA = "getdoc-native-saveas-event-lab/2"
LEGACY_CORE_SCHEMA = "getdoc-native-saveas-private-lab/1"
MAX_RECEIPT_BYTES = 4096
FIXTURES = {
    "gd31j6_writer.docx": "docx",
    "gd31j6_calc.xlsx": "xlsx",
    "gd31j6_impress.pptx": "pptx",
}
GUID = re.compile(r"[0-9a-f]{32}\Z", re.ASCII)
HASH = re.compile(r"[0-9a-f]{64}\Z", re.ASCII)
CURRENT_FIELDS = frozenset({
    "schema", "sessionId", "tabId", "fixtureName",
    "nativeSaveRequestObserved", "savePathProvided", "nativeOnSaveAfterAnswer",
    "saveCancelledAfterAnswer", "sourceUnchanged",
    "publishedToDisposableOutput", "outputSha256",
    "nativeSaveProven", "documentVerified", "saveVerified", "pdfVerified",
    "printApproved", "approvedForProduction", "laboratoryOnly",
})
LEGACY_FIELDS = frozenset({
    "schema", "sessionId", "tabId", "fixtureName", "failureCode",
    "phase", "dialogCount", "onSaveCount", "dialogSequence",
    "onSaveSequence", "receipt",
})
LEGACY_CORE_FIELDS = frozenset({
    "schema", "sessionId", "tabId", "fixtureName", "selectedFormat",
    "sourceBeforeSha256", "sourceAfterSha256", "saveAsRequestSeen",
    "saveAsTargetAccepted", "saveNotification", "saveCancelled",
    "candidateWritten", "candidateName", "candidateLength", "candidateSha256",
    "documentVerified", "saveVerified", "pdfVerified", "printApproved",
    "laboratoryOnly",
})
FAILURES = frozenset({
    "not_started", "dialog_missing", "invalid_dialog", "duplicate_dialog",
    "target_conflict", "cancelled", "timeout", "target_not_assigned",
    "save_not_acknowledged", "source_modified", "private_output_invalid",
    "candidate_missing", "candidate_untrusted",
})
PHASES = frozenset({
    "not_started", "request_sent", "dialog_observed", "target_assigned",
    "on_save_observed", "quarantined",
})


class ProtocolError(ValueError):
    """Any ambiguity is rejected without leaking receipts or document text."""


def _unique(pairs):
    obj = {}
    for key, value in pairs:
        if key in obj:
            raise ProtocolError("Duplicate JSON key")
        obj[key] = value
    return obj


def _exact(value, fields, schema):
    if type(value) is not dict or set(value) != fields or value["schema"] != schema:
        raise ProtocolError("Unsupported SaveAs schema or fields")
    return value


def _txt(obj, key):
    value = obj[key]
    if type(value) is not str:
        raise ProtocolError("Incorrect SaveAs text field")
    return value


def _flag(obj, key):
    value = obj[key]
    if type(value) is not bool:
        raise ProtocolError("Incorrect SaveAs flag")
    return value


def _count(obj, key, maximum):
    value = obj[key]
    if type(value) is not int or not 0 <= value <= maximum:
        raise ProtocolError("Invalid SaveAs event count")
    return value


def _hash(value):
    return type(value) is str and HASH.fullmatch(value) is not None


def _scope(obj, session, tab, fixture):
    if (_txt(obj, "sessionId") != session or _txt(obj, "tabId") != tab or
            _txt(obj, "fixtureName") != fixture):
        raise ProtocolError("SaveAs session/tab/fixture mismatch")


def _new(obj):
    _exact(obj, CURRENT_FIELDS, CURRENT_SCHEMA)
    for flag in CURRENT_FIELDS - {
        "schema", "sessionId", "tabId", "fixtureName", "outputSha256"
    }:
        _flag(obj, flag)
    if (any(_flag(obj, f) for f in (
            "nativeSaveProven", "documentVerified", "saveVerified", "pdfVerified",
            "printApproved", "approvedForProduction")) or
            not _flag(obj, "laboratoryOnly")):
        raise ProtocolError("Unexpected SaveAs verified/production privilege")
    request = _flag(obj, "nativeSaveRequestObserved")
    path = _flag(obj, "savePathProvided")
    saved = _flag(obj, "nativeOnSaveAfterAnswer")
    cancelled = _flag(obj, "saveCancelledAfterAnswer")
    unchanged = _flag(obj, "sourceUnchanged")
    published = _flag(obj, "publishedToDisposableOutput")
    sha = _txt(obj, "outputSha256")
    if ((path and not request) or (saved and not path) or
            (cancelled and not path) or (cancelled and saved) or
            (published and not (request and path and saved and unchanged and not cancelled)) or
            (published and not _hash(sha)) or
            (not published and sha != "")):
        raise ProtocolError("Contradictory pinned native SaveAs candidate")
    return (
        "synthetic_candidate_untrusted" if published else "unconfirmed",
        "candidate_untrusted" if published else "unspecified_failure",
        sha if published else "",
        False,
        "gd31j26-pinned/1",
    )


def _old(obj, session, tab, fixture):
    _exact(obj, LEGACY_FIELDS, LEGACY_SCHEMA)
    _scope(obj, session, tab, fixture)
    code = _txt(obj, "failureCode")
    phase = _txt(obj, "phase")
    if code not in FAILURES or phase not in PHASES:
        raise ProtocolError("Unsupported guarded native status")
    dialog = _count(obj, "dialogCount", 64)
    saved = _count(obj, "onSaveCount", 64)
    first_dialog = _count(obj, "dialogSequence", 128)
    first_save = _count(obj, "onSaveSequence", 128)
    if ((dialog == 0) != (first_dialog == 0) or
            (saved == 0) != (first_save == 0) or
            first_dialog > dialog + saved or first_save > dialog + saved or
            (first_dialog and first_save and first_dialog == first_save)):
        raise ProtocolError("Impossible guarded native event sequence")

    core = _exact(obj["receipt"], LEGACY_CORE_FIELDS, LEGACY_CORE_SCHEMA)
    _scope(core, session, tab, fixture)
    if (_txt(core, "selectedFormat") != FIXTURES[fixture] or
            any(_flag(core, f) for f in (
                "documentVerified", "saveVerified", "pdfVerified", "printApproved")) or
            not _flag(core, "laboratoryOnly")):
        raise ProtocolError("Guarded core format/privileges invalid")
    original = _txt(core, "sourceBeforeSha256")
    if not _hash(original) or _txt(core, "sourceAfterSha256") != original:
        raise ProtocolError("Guarded source digest mismatch")
    requested = _flag(core, "saveAsRequestSeen")
    target = _flag(core, "saveAsTargetAccepted")
    notification = _flag(core, "saveNotification")
    cancelled = _flag(core, "saveCancelled")
    written = _flag(core, "candidateWritten")
    name = _txt(core, "candidateName")
    sha = _txt(core, "candidateSha256")
    length = _count(core, "candidateLength", 128 * 1024 * 1024)
    expected_name = "gd31j26-saveas-candidate-" + session + "." + FIXTURES[fixture]
    if ((target and not requested) or
            (written and not (
                requested and target and notification and not cancelled and
                name == expected_name and length > 0 and _hash(sha) and sha != original)) or
            (not written and (name != "" or length != 0 or sha != "")) or
            (requested and dialog == 0) or
            ((saved > 0) != notification) or
            (written and (phase != "quarantined" or code != "candidate_untrusted" or
                          dialog != 1 or saved != 1 or first_save <= first_dialog)) or
            (not written and phase == "quarantined") or
            (not written and saved > 0 and phase != "on_save_observed") or
            (not written and saved == 0 and target and phase != "target_assigned") or
            (not written and saved == 0 and not target and dialog > 0 and
             phase != "dialog_observed") or
            (code == "candidate_untrusted" and not written) or
            (code == "duplicate_dialog" and dialog < 2) or
            (code == "dialog_missing" and dialog != 0) or
            (code == "cancelled" and not cancelled) or
            (code == "save_not_acknowledged" and saved != 0) or
            (code == "timeout" and saved != 0)):
        raise ProtocolError("Contradictory guarded native event/core receipt")

    return (
        "synthetic_candidate_untrusted" if written else "unconfirmed",
        code, sha if written else "",
        bool(written and first_dialog > 0 and first_save > first_dialog),
        "gd31j28-guarded/2",
    )


def reconcile(raw, expected_session, expected_tab, expected_fixture):
    if (type(raw) is not str or not (2 <= len(raw) <= MAX_RECEIPT_BYTES) or
            any(ord(ch) < 0x20 or ord(ch) > 0x7e for ch in raw)):
        raise ProtocolError("SaveAs receipt is not bounded, single-line ASCII")
    if (type(expected_session) is not str or not GUID.fullmatch(expected_session) or
            int(expected_session, 16) == 0 or
            type(expected_tab) is not str or not GUID.fullmatch(expected_tab) or
            int(expected_tab, 16) == 0 or expected_session == expected_tab or
            expected_fixture not in FIXTURES):
        raise ProtocolError("Unapproved session/tab/fixture")
    try:
        obj = json.loads(raw, object_pairs_hook=_unique,
                         parse_constant=lambda _: (_ for _ in ()).throw(
                             ProtocolError("Nonfinite native JSON")))
    except (UnicodeError, json.JSONDecodeError, TypeError, RecursionError) as exc:
        raise ProtocolError("Malformed SaveAs protocol JSON") from exc
    if type(obj) is not dict:
        raise ProtocolError("Native receipt must be a JSON object")
    schema = obj.get("schema")
    if schema == CURRENT_SCHEMA:
        _scope(obj, expected_session, expected_tab, expected_fixture)
        status, reason, sha, ordered, lineage = _new(obj)
    elif schema == LEGACY_SCHEMA:
        status, reason, sha, ordered, lineage = _old(
            obj, expected_session, expected_tab, expected_fixture)
    else:
        raise ProtocolError("Unsupported or mixed native SaveAs version")
    return {
        "schema": RESULT_SCHEMA,
        "sourceProtocol": lineage,
        "sessionId": expected_session,
        "tabId": expected_tab,
        "fixtureName": expected_fixture,
        "diagnosticState": status,
        "reasonCode": reason,
        "outputSha256": sha,
        "nativeEventOrderSelfReported": ordered,
        "nativeSaveProven": False,
        "documentVerified": False,
        "saveVerified": False,
        "approvedForProduction": False,
        "laboratoryOnly": True,
    }


def from_stream(stream, session, tab, fixture):
    data = stream.read(MAX_RECEIPT_BYTES + 2)
    if (type(data) is not bytes or not 3 <= len(data) <= MAX_RECEIPT_BYTES + 1 or
            data.count(b"\n") != 1 or not data.endswith(b"\n") or
            b"\r" in data):
        raise ProtocolError("Expected exactly one bounded native stdout line")
    try:
        raw = data[:-1].decode("ascii", "strict")
    except UnicodeError as exc:
        raise ProtocolError("SaveAs JSON is not ASCII") from exc
    return reconcile(raw, session, tab, fixture)


def main(argv=None):
    parser = argparse.ArgumentParser(description="Read-only native SaveAs protocol diagnostics")
    parser.add_argument("--session", required=True)
    parser.add_argument("--tab", required=True)
    parser.add_argument("--fixture", required=True, choices=tuple(FIXTURES))
    args = parser.parse_args(argv)
    try:
        report = from_stream(sys.stdin.buffer, args.session, args.tab, args.fixture)
    except ProtocolError as exc:
        print("GD31J27B_REFUSED: " + str(exc), file=sys.stderr)
        return 2
    print(json.dumps(report, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
