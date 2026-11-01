# GD-31J.27d — supervised pinned native v2 process + two runtime library SHA pins

**2026-11-01.** Base GD-31J.27c PR #116 (on #115 → #114 → #113 → #112). Source-only implementation with deferred Python/MSTest/CEF and real offline VM acceptance. **NOT a verified, installable or production document editor.**

## Problem and scope

PR #116 introduced new native CEF event-v2 receipts `getdoc-native-saveas-pinned-event-lab/2`, but the read-only join consumes bytes through stdin. Supplied stdout by itself cannot establish which process produced it; old PR #114 process supervisor launches only `--open-staged-native-saveas-lab` and reads only `candidate-lab/1` with a 2049-byte stdout limit. **Never feed v2 into the legacy supervisor or silently downgrade it to v1.**

This stage introduces a separate entrypoint
`probes/eurooffice-desktop/cef_lab/native_pinned_v2_supervisor.py`:
- Explicit `--i-confirm-disposable-offline-vm` (operator statement, not OS attestation).
- Exactly one hash-pinned absolute native host outside synthetic staging and a new exact argc==6 command:
  `--open-staged-native-saveas-event-v2-lab STAGING FIXTURE SESSION TAB`.
- Exactly two explicitly pinned runtime objects, `libascdocumentscore.so[.N]` and `libqtascdocumentscore.so[.N]`, **in a shared reviewed directory** (as located by CMake). Both must be regular ELF files on nonsymlink local paths, not group/world writable, within 512 MiB; inspect in 1 MiB chunks, hash twice and detect changed size, inode/ctime/mtime. Never accept a user-supplied `LD_LIBRARY_PATH`.
- `nativeBinarySha256` must match a **separately reviewed pin** (not a hash dynamically derived from the file at launch). Rehash host and both libraries after native exit before accepting stdout. Hashes identify bytes seen during reads, **not what CEF/ld.so actually loaded**: loaded modules, Qt platform plugins, child renderer libraries, OS files, signatures, build provenance and sandbox must be independently reviewed. The code does not produce an attestation of those dependencies.
- Restricted child environment: `QT_QPA_PLATFORM=wayland`, optional `WAYLAND_DISPLAY/XDG_RUNTIME_DIR/HOME/LANG/LC_ALL/TMPDIR`, fixed `PATH`, and `LD_LIBRARY_PATH` set **only** to the pinned two libraries' shared directory. No inherited `LD_PRELOAD/LD_AUDIT/PYTHONPATH/QT_PLUGIN_PATH`, network proxy variables or arbitrary custom keys. Absence of proxies is **not** a network isolation mechanism; firewall/VM restrictions remain mandatory.
- Reuses existing GD-31J.27a process primitive `capture_native` with a **new optional** `max_stdout_bytes`. Default remains the previous `join.MAX_NATIVE_BYTES+1` for v1, so the old v1 controller and its explicit CLI are not modified. The new v2 invocation sets `MAX_BYTES+1` (4097 bytes). Uses `shell=False`, no stdin, stderr discarded, max 120 seconds, one private process session, kill process group on failed capture, returns only a bounded stdout byte string and zero exit status.
- Before launch, requires exact approved synthetic fixture, normalized absolute staging and source manifest, `gd31j25-output` directory non-group-writable with marker, and **nonexistent** expected output file (no overwrite). The native CEF process is permitted to write the approved synthetic shadow/output **only inside the manually isolated VM**; this supervisor itself does not create, delete or modify output files.
- After pinned hashes are rechecked, invokes `native_pinned_event_ooxml_join.from_stream` (PR #116), which strictly validates **one full v2 line**, nested v1 candidate, native event-count/ordering self-report, independent on-disk source/output SHA-256, OOXML ZIP/CRC/relationships, canary and source unchanged. There is no fallback to v1 stdout on malformed v2.

## Result contract (not authority)

`getdoc-native-pinned-v2-supervised-ooxml-lab/1` contains:
`sessionId`, `tabId`, `fixtureName`, `nativeBinarySha256`,
`runtimeLibrarySha256: {ascdocumentscore, qtascdocumentscore}`,
`nativeProcessExitZero`, `runtimeLibraryPinsMatched`,
`stdoutSingleLineBounded`, `joinedCandidate` (existing read-only v2 OOXML join),
and permanent `nativeSaveProven=false`, `documentVerified=false`,
`saveVerified=false`, `approvedForProduction=false`, `laboratoryOnly=true`.

`EuroOfficeSupervisedPinnedV2Candidate.Parse` validates **every outer, library and nested field**, duplicate/unknown keys, exact GUID/fixture, SHA formats and separately provided expected three SHA values. It does not verify the real process itself. Nested OOXML checks are still performed **in Python before emission**; parsing fabricated JSON in .NET cannot reproduce that assurance.

## Deferred offline VM command

**NEVER run on production, corporate desktops, user documents, SMB, WORM or a network-enabled host.** Independently prepare offline disposable Linux/Wayland VM, verify pinned EuroOffice SDK and local `getdoc-eurooffice-cef-staged-open-lab` build and two exact runtime .so files, check no corporate shares/credentials, enable opt-in CMake flags, ensure a fresh synthetic staging + marker and correct operator pin manifest. Then:

```sh
QT_QPA_PLATFORM=wayland python3 -B probes/eurooffice-desktop/cef_lab/native_pinned_v2_supervisor.py \
  --staging /ABSOLUTE/DISPOSABLE/GD31J6-STAGING \
  --fixture gd31j6_writer.docx \
  --session 102030405060708090a0b0c0d0e0f010 \
  --tab f010e0d0c0b0a0908070605040302010 \
  --native-binary /ABSOLUTE/REVIEWED/getdoc-eurooffice-cef-staged-open-lab \
  --native-sha256 LOWERCASE_SHA256_FROM_SEPARATELY_REVIEWED_MANIFEST \
  --ascdocumentscore /ABSOLUTE/PINNED/libascdocumentscore.so \
  --ascdocumentscore-sha LOWERCASE_SHA256_FROM_SEPARATELY_REVIEWED_MANIFEST \
  --qtascdocumentscore /ABSOLUTE/PINNED/libqtascdocumentscore.so \
  --qtascdocumentscore-sha LOWERCASE_SHA256_FROM_SEPARATELY_REVIEWED_MANIFEST \
  --i-confirm-disposable-offline-vm
```

Repeat separately for each synthetic DOCX/XLSX/PPTX with a fresh staging tree and **no existing destination**. Only manual VM tests can assess real CEF callback behavior, exact plugin/module loading, local filesystem confinement and OOXML reopen. The synthetic checks do not prove import/editing fidelity, Microsoft Office compatibility, durable atomic fsync, real-life coediting or production Save As.

## Deferred tests

- Python `test_native_pinned_v2_supervisor.py`: positive three formats with fake process, rejection before launch for missing VM approval, wrong Wayland, loader injection, wrong/missing library pins, binary mismatch, existing output, symlink and runtime libraries inside staging. Reject after capture for unexpected stdout, duplicate line, bad digest, forged authority, nonzero exit, binary/library mutation, v1 fallback.
- Python `test_native_pinned_v2_supervisor_contract.py`: strict env/loader isolation, two fixed runtime roles, unchanged v1 default stdout budget, no shell/new filesystem writes, denied production claims.
- .NET MSTest `EuroOfficeSupervisedPinnedV2CandidateTests.cs`: positive diagnostic candidate; unknown/duplicated keys, wrong SHA/role/session/fixture, invalid flags, false callback, mismatched source/output, privileges always denied.
- `scripts/office_acceptance.py` includes two new offline test steps before solution restore plus distinct manual gate `eurooffice-pinned-v2-supervised-native-host-and-runtime-shas-e2e`.
- All tests/builds/SDK/CEF VM E2E intentionally **not executed** in this stage per agreed deferred-testing sequence; this state is NOT "passed", "verified" or "release ready".

## Remaining risks / blockers before consolidation

1. File SHA sampled before/after launch does not atomically bind loaded executable and dynamically linked modules. Independent `/proc/<pid>/maps` evidence, kernel process identity checks, LD/QT plugin coverage and OS permissions require isolated real VM review. Race attacks on path replacement remain possible outside trust boundaries.
2. This hashes exactly two SDK runtime binaries, **not all dependencies**. Additional Qt, CEF, Chromium, libc, media and plugin libraries must be pinned/checked separately before production, including license obligations (EuroOffice SDK/AGPL).
3. The C++ native v2 callback path in PR #116 has not been built or executed: timing, concurrency, nested negative receipts, event budget and callback cancellation need real lab acceptance.
4. One stdout JSON and independent OOXML verification are not cryptographic native provenance or business document-level fidelity. No WORM publishing, writeback, shared editing, printing, PDF or production rights introduced.
5. Draft PRs remain stacked, main/installer/Actions unchanged; only after manual validation choose a single native SaveAs architecture and prepare transactional user saving.

**Next:** GD-31J.27e: state-machine review and negative callback matrix for pending v2 events (source-only first), then isolated VM acceptance and a single reconciled native implementation.
