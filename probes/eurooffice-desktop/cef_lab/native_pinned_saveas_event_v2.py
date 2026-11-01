#!/usr/bin/env python3
"""GD-31J.27c pinned SaveAs optional v2: EXACT read-only native event diagnostics.

Never convert this to legacy guarded /2 or to the old pinned /1 stdout.
The nested pinned /1 is retained byte-for-byte in shape, but negative receipts
may contain the already-known duplicate-after-answer contradiction; hence the
old /1 *candidate* parser is applied only to positive candidates.
"""
from __future__ import annotations

import sys
sys.dont_write_bytecode = True

import json
import re
import native_ooxml_join as join

SCHEMA = "getdoc-native-saveas-pinned-event-lab/2"
MAX_BYTES = 4096
FIELDS = frozenset({
    "schema", "sessionId", "tabId", "fixtureName", "failureCode", "phase",
    "dialogCount", "onSaveCount", "dialogSequence", "onSaveSequence",
    "invalidDialogObserved", "unexpectedOnSaveObserved", "receipt",
})
PHASES = frozenset({
    "not_started", "dialog_observed", "target_assigned",
    "on_save_observed", "published_disposable",
})
FAILURES = frozenset({
    "candidate_untrusted", "invalid_dialog", "duplicate_dialog",
    "unexpected_on_save", "cancelled", "source_modified",
    "dialog_missing", "target_not_assigned", "timeout",
    "save_not_acknowledged", "candidate_missing",
})
SHA = re.compile(r"[0-9a-f]{64}\Z", re.ASCII)


class PinnedEventError(ValueError):
    """Fail closed; do not echo a document, path or raw native receipt."""


def _distinct(pairs):
    obj = {}
    for key, value in pairs:
        if key in obj:
            raise PinnedEventError("Duplicate native event JSON field")
        obj[key] = value
    return obj


def _string(obj, field):
    value = obj[field]
    if type(value) is not str:
        raise PinnedEventError("Native event string type mismatch")
    return value


def _boolean(obj, field):
    value = obj[field]
    if type(value) is not bool:
        raise PinnedEventError("Native event flag type mismatch")
    return value


def _count(obj, field, limit):
    value = obj[field]
    if type(value) is not int or not 0 <= value <= limit:
        raise PinnedEventError("Native event count outside lab budget")
    return value


def _exact(obj, fields, schema):
    if type(obj) is not dict or set(obj) != fields or obj["schema"] != schema:
        raise PinnedEventError("Unexpected native event schema or fields")


def parse(raw, session, tab, fixture):
    if (type(raw) is not str or not 2 <= len(raw) <= MAX_BYTES or
            any(ord(c) < 0x20 or ord(c) > 0x7e for c in raw)):
        raise PinnedEventError("Native v2 must be single-line bounded ASCII")
    if (not join._guid(session) or not join._guid(tab) or session == tab or
            fixture not in join.ooxml.FIXTURES):
        raise PinnedEventError("Native v2 requires approved session/tab/fixture")
    try:
        obj = json.loads(raw, object_pairs_hook=_distinct, parse_constant=lambda _: (
            _ for _ in ()).throw(PinnedEventError("Invalid JSON constant")))
    except (UnicodeError, json.JSONDecodeError, TypeError, RecursionError) as exc:
        raise PinnedEventError("Malformed native v2 JSON") from exc
    _exact(obj, FIELDS, SCHEMA)
    for field, expected in (("sessionId", session), ("tabId", tab),
                            ("fixtureName", fixture)):
        if _string(obj, field) != expected:
            raise PinnedEventError("Native v2 cross-session/fixture mismatch")
    code = _string(obj, "failureCode")
    phase = _string(obj, "phase")
    if code not in FAILURES or phase not in PHASES:
        raise PinnedEventError("Unsupported pinned event failure/phase")
    d = _count(obj, "dialogCount", 64)
    n = _count(obj, "onSaveCount", 64)
    ds = _count(obj, "dialogSequence", 128)
    ns = _count(obj, "onSaveSequence", 128)
    invalid = _boolean(obj, "invalidDialogObserved")
    unexpected = _boolean(obj, "unexpectedOnSaveObserved")
    if ((d == 0) != (ds == 0) or (n == 0) != (ns == 0) or
            ds > d + n or ns > d + n or
            (ds != 0 and ns != 0 and ds == ns) or
            (invalid and d == 0) or (unexpected and n == 0)):
        raise PinnedEventError("Impossible pinned callback sequence")

    base = obj["receipt"]
    _exact(base, join.NATIVE_FIELDS, join.NATIVE_SCHEMA)
    for field, expected in (("sessionId", session), ("tabId", tab),
                            ("fixtureName", fixture)):
        if _string(base, field) != expected:
            raise PinnedEventError("Native nested v1 session/fixture mismatch")
    for field in join.NATIVE_FIELDS - {
        "schema", "sessionId", "tabId", "fixtureName", "outputSha256"
    }:
        _boolean(base, field)
    if (any(_boolean(base, f) for f in (
            "nativeSaveProven", "documentVerified", "saveVerified",
            "pdfVerified", "printApproved", "approvedForProduction")) or
            not _boolean(base, "laboratoryOnly")):
        raise PinnedEventError("Native v1 falsely claims production rights")
    request = _boolean(base, "nativeSaveRequestObserved")
    target = _boolean(base, "savePathProvided")
    saved = _boolean(base, "nativeOnSaveAfterAnswer")
    cancelled = _boolean(base, "saveCancelledAfterAnswer")
    unchanged = _boolean(base, "sourceUnchanged")
    published = _boolean(base, "publishedToDisposableOutput")
    sha = _string(base, "outputSha256")
    if (request != (d == 1) or (target and d == 0) or
            (saved and (n == 0 or not target)) or
            (cancelled and (n == 0 or not target)) or
            (saved and cancelled) or
            (published and (not request or not target or not saved or
                            cancelled or not unchanged)) or
            (published and not SHA.fullmatch(sha)) or
            (not published and sha != "")):
        raise PinnedEventError("Native nested v1 state inconsistent with event telemetry")
    expected_phase = (
        "published_disposable" if published else
        "on_save_observed" if n > 0 else
        "target_assigned" if target else
        "dialog_observed" if d > 0 else "not_started"
    )
    if phase != expected_phase:
        raise PinnedEventError("Native event phase contradicts observed flags")

    if invalid:
        expected_code = "invalid_dialog"
    elif d > 1:
        expected_code = "duplicate_dialog"
    elif unexpected:
        expected_code = "unexpected_on_save"
    elif cancelled:
        expected_code = "cancelled"
    elif not unchanged:
        expected_code = "source_modified"
    elif d == 0:
        expected_code = "dialog_missing"
    elif not target:
        expected_code = "target_not_assigned"
    elif not saved:
        expected_code = "save_not_acknowledged"  # or native timeout marker
    elif not published:
        expected_code = "candidate_missing"
    else:
        expected_code = "candidate_untrusted"

    if (code != expected_code and not (
            expected_code == "save_not_acknowledged" and code == "timeout")):
        raise PinnedEventError("Native failureCode inconsistent with candidate state")

    if published:
        if (d != 1 or n != 1 or ds != 1 or ns != 2 or invalid or
                unexpected or not saved or cancelled):
            raise PinnedEventError("Published lab candidate lacks unique ordered callback pair")
        # Existing strict v1 validator is authoritative for positive candidate.
        try:
            join._receipt(json.dumps(base, separators=(",", ":")),
                          fixture, session, tab)
        except join.JoinError as exc:
            raise PinnedEventError("Nested successful v1 candidate failed validation") from exc
    return {
        "schema": SCHEMA,
        "sessionId": session, "tabId": tab, "fixtureName": fixture,
        "failureCode": code, "phase": phase,
        "dialogCount": d, "onSaveCount": n,
        "dialogSequence": ds, "onSaveSequence": ns,
        "invalidDialogObserved": invalid, "unexpectedOnSaveObserved": unexpected,
        "diagnosticState": "synthetic_candidate_untrusted" if published else "unconfirmed",
        "outputSha256": sha if published else "",
        "nativeCallbackSequenceSelfReported": published,
        "nativeSaveProven": False, "documentVerified": False,
        "saveVerified": False, "approvedForProduction": False,
        "laboratoryOnly": True,
    }
