#!/usr/bin/env python3
"""GD31J27i — READ-ONLY offline SaveAs evidence gate; NEVER grants release.

No SDK invocation, subprocesses, tests, file creation, cleanup, CI or business
document access. Report and operator evidence are locally claimed and are NOT
independent attestation. Even all-green inputs yield only "requires-independent-
review", not save, WORM, production, install or merge authorization.
"""
from __future__ import annotations

import sys
sys.dont_write_bytecode = True

import argparse
import json
import os
from pathlib import Path
import re
import stat

# source-only reuse of the GD31I acceptance catalog, not its test execution.
SCRIPTS = Path(__file__).resolve().parents[3] / "scripts"
sys.path.insert(0, str(SCRIPTS))
import office_acceptance as acceptance
import synthetic_roundtrip as ooxml

PLAN_SCHEMA = "getdoc-gd31j27i-native-saveas-offline-plan-lab/1"
EVIDENCE_SCHEMA = "getdoc-gd31j27i-native-saveas-manual-evidence-lab/1"
RESULT_SCHEMA = "getdoc-gd31j27i-native-saveas-independent-review-lab/1"
MAX_JSON_BYTES = 64 * 1024
SHA40 = re.compile(r"[0-9a-f]{40}\Z", re.ASCII)
SHA64 = re.compile(r"[0-9a-f]{64}\Z", re.ASCII)
FIXTURES = tuple(ooxml.FIXTURES)

# These are distinct manual fault-injection tests, NOT a script to perform
# dangerous faults outside an OS-isolated disposable Linux/Wayland VM.
CASES = (
    "offline-vm-and-no-corporate-mounts",
    "pinned-native-executable-and-runtime-sha",
    "docx-independent-ooxml-zip-crc-canary",
    "xlsx-independent-ooxml-zip-crc-canary",
    "pptx-independent-ooxml-zip-crc-canary",
    "early-and-duplicate-cef-callbacks",
    "callback-storm-overflow-and-teardown",
    "existing-final-never-overwritten",
    "source-name-swap-during-linkat",
    "same-uid-writer-after-anonymous-snapshot",
    "worker-fd-only-distinct-uid-mount-net-seccomp",
    "broker-peercred-one-shot-fd-handoff-no-privileges",
    "worker-live-proc-status-namespace-mountinfo-caps-deny",
    "supervisor-peercred-pid-start-exact-fd3-table",
    "post-uid-broker-bind-worker-connect-fd3-peercred",
    "worker-anonymous-snapshot-crash-fsync-linkat-unknown-outcome",
    "root-supervisor-scm-rights-worker-owned-fd-relay-deny",
    "three-party-root-broker-worker-session-fd-close-denial",
    "root-fixed-manifest-synthetic-fd-sender-no-extra-dac",
    "root-broker-pid-reuse-dac-caps-mountns-deny",
    "scratch-and-final-on-different-filesystems",
    "file-fsync-denied-before-linkat",
    "directory-fsync-denied-after-linkat",
    "process-killed-before-linkat",
    "process-killed-after-linkat-before-dir-fsync",
    "process-killed-after-dir-fsync-before-scratch-cleanup",
    "orphan-hardlink-inode-manual-review-only",
    "unrecognized-files-symlinks-never-deleted",
    "v1-regression-and-no-v2-downgrade",
)

# Explicit list: never silently drop an E2E gate when its name changes.
REQUIRED_MANUAL_GATES = (
    "eurooffice-native-sdk-fixture-binding-and-import-e2e",
    "eurooffice-native-converter-private-artifact-candidate-e2e",
    "eurooffice-content-ready-patched-sdk-runtime-e2e",
    "eurooffice-synthetic-ooxml-native-saveas-canary-e2e",
    "eurooffice-native-saveas-pinned-sdk-output-isolation-e2e",
    "eurooffice-native-ooxml-session-sha-and-canary-join-e2e",
    "eurooffice-supervised-native-saveas-pin-stdout-and-ooxml-e2e",
    "eurooffice-v1-v2-saveas-protocol-lineage-reconciliation-e2e",
    "eurooffice-pinned-native-v2-callback-counts-and-ooxml-join-e2e",
    "eurooffice-pinned-v2-supervised-native-host-and-runtime-shas-e2e",
    "eurooffice-pinned-v2-state-machine-early-duplicate-cancel-timeout-e2e",
    "eurooffice-pinned-v2-bounded-callback-shutdown-operator-ui-e2e",
    "eurooffice-gd31j27g-fsync-no-replace-crash-recovery-e2e",
    "eurooffice-gd31j27h-v1-v2-native-ooxml-crash-lineage-e2e",
    "eurooffice-gd31j27j-fd-bound-link-no-path-swap-e2e",
    "eurooffice-gd31j27k-owner-only-unlinked-snapshot-same-uid-writer-e2e",
    "eurooffice-gd31j27l-distinct-uid-fd3-worker-isolation-e2e",
    "eurooffice-gd31j27m-external-broker-fd-handoff-isolation-e2e",
    "eurooffice-gd31j27n-live-linux-kernel-sandbox-observer-e2e",
    "eurooffice-gd31j27o-supervisor-peercred-pid-start-fd3-handoff-e2e",
    "eurooffice-gd31j27p-post-uid-broker-worker-socket-lab-e2e",
    "eurooffice-gd31j27q-worker-anonymous-snapshot-fsync-linkat-crash-e2e",
    "eurooffice-gd31j27r-root-supervisor-worker-owned-fd-relay-e2e",
    "eurooffice-gd31j27s-three-party-denial-bridge-fd-lifecycle-e2e",
    "eurooffice-gd31j27t-root-supervisor-fixed-fd-sender-e2e",
    "eurooffice-gd31j27u-root-broker-pid-dac-namespace-audit-e2e",
)
REQUIRED_AUTOMATED_STEPS = tuple(step.name for step in
    acceptance.build_steps(Path("/unused"), Path("/offline-feed"), "Linux"))


class GateError(ValueError):
    """Do not echo evidence, user-supplied paths or document contents."""


def _exact(value, fields):
    if type(value) is not dict or set(value) != set(fields):
        raise GateError("Unexpected acceptance schema fields")


def _pairwise_unique(pairs):
    obj = {}
    for name, value in pairs:
        if name in obj:
            raise GateError("Duplicate acceptance JSON field")
        obj[name] = value
    return obj


def read_json_file(path: Path | str):
    """Read only a protected, unlinked local JSON file; no effect on its contents."""
    file = Path(path)
    try:
        ooxml._must_be_unlinked_local(file)
        flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | \
            getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_NONBLOCK", 0)
        fd = os.open(file, flags)
        with os.fdopen(fd, "rb") as stream:
            before = os.fstat(stream.fileno())
            if (not stat.S_ISREG(before.st_mode) or before.st_nlink != 1 or
                    before.st_mode & 0o022 or not 2 <= before.st_size <= MAX_JSON_BYTES):
                raise GateError("Acceptance input must be a protected regular JSON file")
            raw = stream.read(MAX_JSON_BYTES + 1)
            after = os.fstat(stream.fileno())
            if (len(raw) != before.st_size or
                    before.st_dev != after.st_dev or before.st_ino != after.st_ino or
                    before.st_size != after.st_size or
                    before.st_mtime_ns != after.st_mtime_ns or
                    before.st_ctime_ns != after.st_ctime_ns):
                raise GateError("Acceptance JSON changed during read")
    except (OSError, ooxml.LabError) as exc:
        raise GateError("Unable to read approved offline acceptance JSON") from exc
    try:
        return json.loads(raw.decode("utf-8", "strict"),
                          object_pairs_hook=_pairwise_unique,
                          parse_constant=lambda _: (_ for _ in ()).throw(
                              GateError("Non-finite acceptance JSON constant")))
    except (UnicodeError, json.JSONDecodeError, RecursionError) as exc:
        raise GateError("Malformed offline acceptance JSON") from exc


def _check_report(report, revision):
    if type(report) is not dict:
        raise GateError("Missing automated source report")
    required = (
        "schemaVersion", "stage", "revision", "host", "status",
        "automatedChecksPassed", "releaseReady", "manualAcceptance", "steps",
    )
    # GD31I reports contain optional preflight, createdUtc, OS build details.
    if not set(required).issubset(report):
        raise GateError("Missing automated source report fields")
    if (type(report["schemaVersion"]) is not int or report["schemaVersion"] != 1 or
            report["stage"] != "GD-31I" or report["host"] != "Linux" or
            report["revision"] != revision or
            report["status"] != "automated-checks-passed" or
            report["automatedChecksPassed"] is not True or
            report["releaseReady"] is not False):
        raise GateError("Automated source report cannot be used for native VM review")
    if not set(REQUIRED_MANUAL_GATES).issubset(set(acceptance.MANUAL_GATES)):
        raise GateError("One or more native manual acceptance gates disappeared")
    manual = report["manualAcceptance"]
    _exact(manual, acceptance.MANUAL_GATES)
    if any(status != "not-run" for status in manual.values()):
        # GD31I's own report never claims that a manual gate has passed.
        raise GateError("Automated report claims unverified manual acceptance")
    steps = report["steps"]
    if type(steps) is not list or len(steps) < len(REQUIRED_AUTOMATED_STEPS):
        raise GateError("Automated report has missing steps")
    observed = set()
    for step in steps:
        if type(step) is not dict or "name" not in step:
            raise GateError("Malformed automated step")
        name = step["name"]
        if type(name) is not str or name in observed:
            raise GateError("Duplicate or unrecognized automated step")
        observed.add(name)
        if (step.get("status") != "passed" or
                type(step.get("exitCode")) is not int or
                step["exitCode"] != 0):
            raise GateError("Automated step failed or was not run")
    if not set(REQUIRED_AUTOMATED_STEPS).issubset(observed):
        raise GateError("Expected native offline acceptance step missing")


def _check_evidence(evidence):
    _exact(evidence, {
        "schema", "revision", "protocol", "fixtureNames", "isolatedVm", "cases",
    })
    if (evidence["schema"] != EVIDENCE_SCHEMA or
            type(evidence["revision"]) is not str or
            not SHA40.fullmatch(evidence["revision"]) or
            evidence["protocol"] != "getdoc-native-saveas-pinned-event-lab/2" or
            evidence["fixtureNames"] != list(FIXTURES)):
        raise GateError("Manual evidence protocol, revision or fixture is not pinned")
    isolation = evidence["isolatedVm"]
    _exact(isolation, {
        "offline", "disposable", "corporateMountsAbsent",
        "privilegedSecretsAbsent",
    })
    if any(value is not True for value in isolation.values()):
        raise GateError("Offline disposable VM isolation not operator-confirmed")
    cases = evidence["cases"]
    _exact(cases, CASES)
    for item in cases.values():
        _exact(item, {"status", "evidenceSha256", "reviewedByOperator"})
        status = item["status"]
        digest = item["evidenceSha256"]
        reviewed = item["reviewedByOperator"]
        if (status not in ("not-run", "passed", "failed", "blocked") or
                type(status) is not str or type(digest) is not str or
                type(reviewed) is not bool):
            raise GateError("Invalid fault-injection case type/status")
        if status == "passed":
            if not SHA64.fullmatch(digest) or not reviewed:
                raise GateError("Passing case lacks operator-reviewed evidence digest")
        elif digest or reviewed:
            raise GateError("Unsuccessful or unrun case must not claim evidence")


def plan():
    return {
        "schema": PLAN_SCHEMA,
        "selectedProtocol": "getdoc-native-saveas-pinned-event-lab/2",
        "fixtures": list(FIXTURES),
        "cases": {name: "not-run" for name in CASES},
        "requiredManualGates": list(REQUIRED_MANUAL_GATES),
        "releaseReady": False,
        "approvedForProduction": False,
        "writePermissionGranted": False,
        "safeToDeleteAutomatically": False,
        "laboratoryOnly": True,
    }


def evaluate(report, evidence):
    """Even all passing evidence is ONLY a pending, independently reviewed claim."""
    _check_evidence(evidence)
    _check_report(report, evidence["revision"])
    cases = evidence["cases"]
    blockers = [name for name in CASES if cases[name]["status"] != "passed"]
    claims_passed = len(CASES) - len(blockers)
    return {
        "schema": RESULT_SCHEMA,
        "revision": evidence["revision"],
        "protocol": evidence["protocol"],
        "reviewState": "blocked" if blockers else "requires-independent-review",
        "operatorClaimsPassed": claims_passed,
        "blockingCases": blockers,
        "requiredManualGates": list(REQUIRED_MANUAL_GATES),
        "nativeSaveProven": False,
        "documentVerified": False,
        "saveVerified": False,
        "releaseReady": False,
        "approvedForProduction": False,
        "writePermissionGranted": False,
        "safeToDeleteAutomatically": False,
        "laboratoryOnly": True,
    }


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="GD31J27i: read-only manual evidence inspection, NEVER release authorization")
    modes = parser.add_mutually_exclusive_group(required=True)
    modes.add_argument("--plan", action="store_true")
    modes.add_argument("--evaluate", action="store_true")
    parser.add_argument("--automated-report", type=Path)
    parser.add_argument("--manual-evidence", type=Path)
    args = parser.parse_args(argv)
    if args.plan:
        print(json.dumps(plan(), sort_keys=True, separators=(",", ":")))
        return 0
    try:
        if not args.automated_report or not args.manual_evidence:
            raise GateError("Both locally reviewed evidence paths are required")
        result = evaluate(read_json_file(args.automated_report),
                          read_json_file(args.manual_evidence))
        print(json.dumps(result, sort_keys=True, separators=(",", ":")))
    except GateError as exc:
        print("GD31J27I_BLOCKED: " + str(exc), file=sys.stderr)
    # Always nonzero. This script never blesses a build, native SaveAs, merge or release.
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
