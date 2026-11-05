#!/usr/bin/env python3
"""GD-31J.27br: read-only inventory of BLOCKERS, never a release approver.

Reads the literal manual gate catalog from office_acceptance.py and optionally
an existing offline acceptance JSON. It does not run tests, open documents,
write files, call the network, or translate "passed" claims into approval.
"""
from __future__ import annotations

import argparse
import ast
import json
from pathlib import Path
import re
import sys
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
MAX_INPUT_BYTES = 1024 * 1024
SHA1 = re.compile(r"[0-9a-f]{40}\Z")


def load_required_gates(root: Path) -> tuple[str, ...]:
    """Parse a literal tuple rather than executing the acceptance runner."""
    catalog = root / "scripts" / "office_acceptance.py"
    tree = ast.parse(catalog.read_text(encoding="utf-8"), filename=str(catalog))
    definitions = [node.value for node in tree.body
                   if isinstance(node, ast.Assign) and
                   any(isinstance(target, ast.Name) and target.id == "MANUAL_GATES"
                       for target in node.targets)]
    if len(definitions) != 1 or not isinstance(definitions[0], ast.Tuple):
        raise ValueError("MANUAL_GATES must have one literal tuple definition")
    nodes = definitions[0].elts
    if any(not isinstance(node, ast.Constant) or
           not isinstance(node.value, str) or not node.value
           for node in nodes):
        raise ValueError("Every manual gate must be a nonempty literal string")
    gates = tuple(node.value for node in nodes)
    if not gates or len(gates) != len(set(gates)):
        raise ValueError("Manual gates are empty or duplicated")
    return gates


def _read_existing_report(path: Path) -> dict[str, Any]:
    if path.is_symlink() or not path.is_file() or path.stat().st_size > MAX_INPUT_BYTES:
        raise ValueError("Report must be a regular, bounded, non-symlink local file")
    try:
        report = json.loads(path.read_text(encoding="utf-8"))
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise ValueError("Invalid report JSON") from exc
    if not isinstance(report, dict) or report.get("schemaVersion") != 1:
        raise ValueError("Expected acceptance report schemaVersion=1")
    if report.get("host") not in ("Windows", "Linux"):
        raise ValueError("Report host must be Windows or Linux")
    if not isinstance(report.get("manualAcceptance"), dict):
        raise ValueError("Missing manualAcceptance mapping")
    if not isinstance(report.get("revision"), str) or not SHA1.fullmatch(report["revision"]):
        raise ValueError("Report must identify an exact 40-character Git revision")
    return report


def _group(name: str) -> str:
    if name.startswith("gd31j"):
        return "recovery"
    if name.startswith("eurooffice-"):
        return "nativeEuroOffice"
    return "other"


def analyze(root: Path, report_path: Path | None = None,
            expected_revision: str | None = None, details: bool = False) -> dict[str, Any]:
    gates = load_required_gates(root)
    report: dict[str, Any] | None = None
    if report_path is not None:
        if not isinstance(expected_revision, str) or not SHA1.fullmatch(expected_revision):
            raise ValueError("--expected-revision must be a known, exact Git SHA")
        report = _read_existing_report(report_path)
        if report["revision"] != expected_revision:
            raise ValueError("Stale or mismatched acceptance report revision")
    counts = {"total": len(gates), "noEvidence": 0, "notRun": 0,
              "failed": 0, "claimedPassedUnverified": 0,
              "missing": 0, "invalid": 0}
    groups = {"recovery": 0, "nativeEuroOffice": 0, "other": 0}
    warnings: list[str] = []
    statuses = {} if report is None else report["manualAcceptance"]
    if report is not None and report.get("releaseReady") is True:
        warnings.append("report-claims-release-ready")
    if set(statuses) - set(gates):
        warnings.append("unknown-report-gates")

    blockages: list[dict[str, str]] = []
    priority: list[str] = []
    for gate in gates:
        groups[_group(gate)] += 1
        if report is None:
            state, bucket = "no-evidence", "noEvidence"
        elif gate not in statuses:
            state, bucket = "missing-from-report", "missing"
        else:
            raw = statuses[gate]
            if raw == "not-run":
                state, bucket = "not-run", "notRun"
            elif raw == "failed":
                state, bucket = "failed", "failed"
            elif raw == "passed":
                state, bucket = "claimed-passed-unverified", "claimedPassedUnverified"
            else:
                state, bucket = "invalid-report-status", "invalid"
        counts[bucket] += 1
        if details:
            blockages.append({"gate": gate, "state": state})
        if (gate.startswith(("gd31j27bp-", "gd31j27bq-")) or
            gate in ("corporate-sso-acl-and-worm-cas",
                     "smb-conflicts-and-emergency-recovery")):
            priority.append(gate)

    result: dict[str, Any] = {
        "stage": "GD-31J.27br", "status": "blocked", "releaseReady": False,
        "note": "Inventory only: even 'passed' report entries are NOT independent attestation",
        "reportRevision": None if report is None else report["revision"],
        "manualGates": counts, "groups": groups,
        "priorityUnverified": priority, "warnings": warnings,
    }
    if details:
        result["blockingGates"] = blockages
    return result


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="GD-31J.27br read-only release blocker inventory")
    parser.add_argument("--root", type=Path, default=ROOT,
                        help="Repository checkout (default: script's parent repo)")
    parser.add_argument("--report", type=Path,
                        help="Existing local JSON created by office_acceptance.py")
    parser.add_argument("--expected-revision",
                        help="Required exact Git SHA when supplying --report")
    parser.add_argument("--details", action="store_true",
                        help="Show each manual gate and its unverified status")
    args = parser.parse_args(argv)
    try:
        result = analyze(args.root, args.report, args.expected_revision, args.details)
    except (OSError, ValueError, SyntaxError) as exc:
        print(f"BLOCKED: {exc}", file=sys.stderr)
        return 2
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0  # success generating the blocker inventory, NEVER release approval


if __name__ == "__main__":
    raise SystemExit(main())
