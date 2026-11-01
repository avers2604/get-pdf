# GD-31J.27a — pinned native SaveAs stdout supervisor (synthetic, offline LAB ONLY)

**2026-11-01.** Stacked on PR #113 `feature/gd31j27-correlated-native-ooxml-candidate`. Source and deferred tests only. **No tests, Qt/CEF, real SaveAs, build or VM executed.**

## Why this is distinct from GD-31J.27

GD-31J.27 (#113) checks one untrusted native stdout JSON receipt against the actual synthetic OOXML output using GD-31J.25. Direct stdin from an operator/file **cannot establish native process provenance**. GD-31J.27a adds an **opt-in, fail-closed process supervisor** that runs one exact native executable from a separately pinned binary hash, collects at most a single bounded stdout line, requires normal exit, rehashes the binary, then independently performs the same OOXML/canary/source checks in memory. The supervisor issues a *different* unprivileged JSON envelope; it never upgrades the old receipt to an authenticated document-save certificate.

## Exact trust boundary and mechanical controls

- Scope: three `gd31j6_*.docx/xlsx/pptx` synthetic fixtures only; absolute staging, marker `gd31j25-output-lab.marker`, **nonexistent** exact target path before launch, unique nonzero lowercase session/tab GUID32.
- Manual opt-in `--i-confirm-disposable-offline-vm`, pre-existing `QT_QPA_PLATFORM=wayland`. **Neither argument attests actual VM isolation**; an operator must separately verify the absence of network, corporate mounts/SMB/GUP-2/WORM/user data, and privileges.
- Requires native executable outside staging, absolute path without symlink components, executable bit, not group/world writable and SHA-256 identical to a **separately reviewed binary pin**. Two digest samples (before and after) only detect routine modification; no kernel-level executable/loaded-library attestation. Operators must verify the pinned vendor SDK/source, rebuild artifacts, dynamic libraries, AGPLv3+exceptions and OS sandbox independently.
- Process invoked via `subprocess.Popen(argv list, shell=False)`, no stdin, stderr discarded, stdout bounded to 2049 bytes, local Linux Wayland only, 120 s maximum. On interrupted collection, terminate the private child process group (including helpers holding stdout). No generic filename, file-write API, shell, or network operation is exposed by the supervisor itself.
- Arguments to the existing native Qt/CEF executable match its **exact** GD31J26 `argc==6` mode: `--open-staged-native-saveas-lab STAGING FIXTURE SESSION TAB`. The native host may write **only its previously approved disposable output**; this is why it must never execute against real documents or the corporate workstation.
- Requires zero exit status and one terminated ASCII native receipt. Calls existing `native_ooxml_join.correlate_from_stream`: exact native schema, source and output SHA-256, ZIP CRC, OOXML parts and relationships, one canary at a visible text node, unchanged source, repeat reads. No file paths or content in the emitted receipt.
- New envelope schema `getdoc-native-ooxml-supervised-candidate-lab/1` contains binary SHA, positive *self-described local process observations*, and a nested joined candidate. `EuroOfficeSupervisedNativeOoxmlCandidate.Parse` strictly checks all outer fields and delegates nested data to `EuroOfficeNativeOoxmlJoinCandidate.Parse`.
- **`nativeSaveProven=false`, `documentVerified=false`, `saveVerified=false`, `approvedForProduction=false`** always, regardless of success. A perfectly forged output envelope remains forgeable by a process with the right write access. The presence of an expected hash is not a digital signature or authentic origin proof.
- Python source-only verifier disables bytecode generation before loading its local module; recommend `python3 -B` for all offline lab executions. The supervisor does not write receipts, documents, temp files or logs.

## Deferred offline command (VM ONLY — do not run on corporate hosts)

```sh
QT_QPA_PLATFORM=wayland python3 -B probes/eurooffice-desktop/cef_lab/native_ooxml_supervisor.py \
  --staging /ABSOLUTE/ISOLATED/DISPOSABLE/STAGING \
  --fixture gd31j6_writer.docx \
  --session 102030405060708090a0b0c0d0e0f010 \
  --tab f010e0d0c0b0a0908070605040302010 \
  --native-binary /ABSOLUTE/REVIEWED/cef_staged_open \
  --native-sha256 THE_LOWERCASE_SHA256_FROM_SEPARATELY_REVIEWED_MANIFEST \
  --i-confirm-disposable-offline-vm
```

Run for each synthetic fixture in a **fresh staging directory** (never reuse the target). Keep separate operator evidence for source pin, actual VM isolation, interactive editor action and third-party reopen. Failure is a blocked lab candidate, not permission to attempt production fallback.

## Deferred regression coverage

- New `test_native_ooxml_supervisor.py`: positive synthetic DOCX, rejected no opt-in/wrong Wayland/pin, existing output, nonzero exit, extra stdout, binary changed during capture, bad callback, canceled native event, wrong output digest, forged privilege.
- `test_native_ooxml_supervisor_contract.py`: native argv, no shell, bounded capture, no bytecode by local imports, strict .NET parser, permanent denied production privileges.
- `EuroOfficeSupervisedNativeOoxmlCandidateTests.cs`: nested and outer duplicate keys, wrong pin/session/fixture, false process conditions, altered output, privilege escalation.
- `scripts/office_acceptance.py`: optional offline unit/source gates **before** restore; a separate **manual-only** Linux/Wayland E2E gate. GitHub Actions, installers, `main`, production SaveAs, WORM and GUP-2 unchanged.

## Parallel PR lineage: do not mix stages by their numeric labels

| Lineage | PRs | Distinguishing work | Integration plan |
|---|---|---|---|
| Earlier guarded SaveAs | #99 (GD-31J.27) on #98 → #100 (GD-31J.28) | `argc==7` routing, SaveLock/event lifecycle and richer native event/failure codes | Compare event observer/gates against newer native implementation. Port **tested behavior**, not blind commits. |
| New native pinned SaveAs | #112 (GD-31J.26) on #111 and #96 | `argc==6` native vendor SaveAs, private output and source digest | Maintained as separate, pinned synthetic laboratory baseline. |
| Independent ZIP join | #113 (GD-31J.27) on #112 | untrusted native stdout + independent OOXML SHA/CRC/canary | Base for this GD-31J.27a. |
| Supervised source capture | **this PR, GD-31J.27a** on #113 | invoke one hash-pinned native host and correlate with read-only OOXML | Do not merge to `main` until lineage reconciliation and VM acceptance. |

**Critical incompatibility:** `#99`'s seven-argument event/failure protocol and `#112`'s six-argument synthetic SaveAs are not interchangeable. No automatic merge, event-schema downgrade, or broad fallback is approved. A dedicated compatibility table and local runtime tests are required before choosing a consolidated native path.

## Unverified acceptance blockers

1. Independent SHA pin of native host **and dynamically loaded libraries**, OS process identity, sandbox and isolation of disposable VM.
2. Local Python/MSTest/source-gate execution; .NET 10 build and real Qt/CEF pinned SDK build, including AGPL license review.
3. Real manual DOCX/XLSX/PPTX Save As via CEF with native `OnSave`, one canary, uncontaminated source, process exit and reopened package.
4. Attack/fault matrix: race replacing host or output, hung child/grandchild, timestamp/ctime tampering, forged stdout, canceled or duplicated callbacks, ZIP corruption, wrong session, output conflict, source mutation.
5. Microsoft Office compatibility and production save/rollback/collaboration acceptance remain separate activities.

**Status:** implementation prepared, **not run**; no release claim, no production privileges, no merged code.
