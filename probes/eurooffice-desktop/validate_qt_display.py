#!/usr/bin/env python3
"""Validate Qt-only local diagnostic evidence; NEVER grant release approval.

Even a passing report is preliminary because a software report cannot attest
which physical monitors were connected or whether a human changed system DPI.
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import sys


class EvidenceRejected(ValueError):
    pass


EXPECTED_KEYS = {
    "protocol", "runtimeKind", "closed", "cefInitialized",
    "realMonitorVerified", "manualMode", "screenCount", "screens",
    "windowEvents",
}
EVENT_KEYS = {"show", "resize", "move", "focus", "screenChanged"}
SCREEN_KEYS = {"width", "height", "devicePixelRatio", "logicalDpi"}


def _int(value: object, minimum: int, maximum: int, name: str) -> int:
    if type(value) is not int or not minimum <= value <= maximum:
        raise EvidenceRejected(f"Invalid {name}")
    return value


def _positive_number(value: object, maximum: float, name: str) -> float:
    if type(value) not in (int, float) or not math.isfinite(value) or not 0 < value <= maximum:
        raise EvidenceRejected(f"Invalid {name}")
    return float(value)


def require_physical_display_evidence(report: dict) -> dict:
    if not isinstance(report, dict) or set(report) != EXPECTED_KEYS:
        raise EvidenceRejected("Unknown or missing diagnostic report fields")
    if (report["protocol"] != "getdoc-qt-display-diagnostics/1" or
        report["runtimeKind"] != "qt-only-no-cef" or
        report["manualMode"] is not True or
        report["closed"] is not True or
        report["cefInitialized"] is not False or
        report["realMonitorVerified"] is not False):
        raise EvidenceRejected("Not a completed manual Qt-only display check")

    count = _int(report["screenCount"], 2, 16, "screenCount")
    screens = report["screens"]
    if not isinstance(screens, list) or len(screens) != count:
        raise EvidenceRejected("Screen list does not match the count")
    for screen in screens:
        if not isinstance(screen, dict) or set(screen) != SCREEN_KEYS:
            raise EvidenceRejected("Unexpected screen fields")
        _int(screen["width"], 1, 32768, "width")
        _int(screen["height"], 1, 32768, "height")
        _positive_number(screen["devicePixelRatio"], 10.0, "devicePixelRatio")
        _positive_number(screen["logicalDpi"], 2000.0, "logicalDpi")

    events = report["windowEvents"]
    if not isinstance(events, dict) or set(events) != EVENT_KEYS:
        raise EvidenceRejected("Invalid Qt window events")
    for key in EVENT_KEYS:
        _int(events[key], 0, 100000, key)
    if (events["show"] < 1 or events["resize"] < 1 or
        events["move"] < 1 or events["focus"] < 1 or
        events["screenChanged"] < 1):
        raise EvidenceRejected("Two-screen movement/focus events not observed")

    # A valid report is NOT CEF verification, a physical screenshot, approval
    # of native embedding, or an authorized software release.
    return {
        "status": "preliminary-manual-evidence",
        "cefVerified": False,
        "avaloniaEmbedded": False,
        "releaseReady": False,
    }


def _reject_duplicate_keys(pairs):
    result = {}
    for name, value in pairs:
        if name in result:
            raise EvidenceRejected("Duplicate JSON key")
        result[name] = value
    return result


def _reject_json_constant(value: str):
    raise EvidenceRejected("Invalid JSON number: " + value)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Validate local two-monitor Qt-only evidence")
    parser.add_argument("report", type=Path, help="JSON receipt from --display-live")
    args = parser.parse_args(argv)
    try:
        if args.report.is_symlink() or args.report.stat().st_size > 65536:
            raise EvidenceRejected("Invalid local report source")
        raw = args.report.read_text(encoding="utf-8")
        report = json.loads(raw, object_pairs_hook=_reject_duplicate_keys,
                            parse_constant=_reject_json_constant)
        result = require_physical_display_evidence(report)
    except (OSError, UnicodeError, ValueError, TypeError) as exc:
        print("GD-31J.5 DISPLAY EVIDENCE REJECTED:", exc, file=sys.stderr)
        return 2
    print(json.dumps(result, ensure_ascii=False))
    print("Manual physical observation and real CEF/Avalonia acceptance STILL REQUIRED.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
