#!/usr/bin/env python3
"""GD-31I: offline-only, reproducible LOCAL acceptance runner for GET Documents.

This is a developer/IT tool, NOT a production dependency or a release approval.
Never downloads tools, connects to the Internet, enables CI, or edits documents.
Use --feed <approved local NuGet folder>; --plan never launches build tools.
"""
from __future__ import annotations

import argparse
import ast
from dataclasses import dataclass
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import time
import uuid


REPO_ROOT = Path(__file__).resolve().parents[1]

TEST_PROJECTS = {
    "core": "tests/GetDocument.Core.Tests/GetDocument.Core.Tests.csproj",
    "office": "tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj",
    "images": "tests/GetDocument.Images.Tests/GetDocument.Images.Tests.csproj",
    "revisions": "tests/GetDocument.Revisions.Tests/GetDocument.Revisions.Tests.csproj",
    "collaboration": "tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj",
    "wopi-gateway": "tests/GetDocument.WopiGateway.Tests/GetDocument.WopiGateway.Tests.csproj",
}
# These are genuinely manual/hardware/backend gates, NOT inferred from green MSTest.
MANUAL_GATES = (
    "windows-get-desktop-editor-and-tabs",
    "linux-astra-desktop-and-dpi",
    "real-libreoffice-uno-writeback-and-xlsx-roundtrip",
    "eurooffice-desktop-embedding-api",
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
    "eurooffice-gd31j27v-pidfd-broker-liveness-identity-e2e",
    "eurooffice-gd31j27w-worker-pidfd-scm-rights-deny-e2e",
    "eurooffice-gd31j27x-socket-peerpidfd-identity-e2e",
    "eurooffice-gd31j27y-fd3-nonce-proc-inode-rights-free-e2e",
    "eurooffice-gd31j27z-root-pinned-exe-cgroup-launch-review-e2e",
    "eurooffice-gd31j27aa-dependency-elf-unit-pins-review-e2e",
    "eurooffice-gd31j27ab-ed25519-sbom-effective-unit-review-e2e",
    "eurooffice-gd31j27ac-wheel-record-elf-systemd-os-snapshot-e2e",
    "eurooffice-gd31j27ad-preopened-proc-maps-elf64-deny-e2e",
    "eurooffice-gd31j27ae-pidfd-fd-origin-namespace-elf-hash-e2e",
    "eurooffice-gd31j27af-real-fixed-fd-proc-elf-readonly-e2e",
    "eurooffice-gd31j27ag-independent-triparty-denial-trace-vm-e2e",
    "eurooffice-gd31j27ah-distinct-uid-devnull-scm-rights-manual-e2e",
    "eurooffice-gd31j27ak-windows-currentuser-pid-denial-manual-e2e",
    "gd31j27al-windows-ntfs-real-dotnet-savecopy-manual-e2e",
    "gd31j27al-linux-ext4-real-dotnet-savecopy-manual-e2e",
    "gd31j27al-offline-smb-real-dotnet-savecopy-manual-e2e",
    "gd31j27ao-recovery-inventory-windows-ntfs-manual-e2e",
    "gd31j27ao-recovery-inventory-linux-astra-manual-e2e",
    "gd31j27ap-recovery-diagnostics-windows-desktop-manual-e2e",
    "gd31j27ap-recovery-diagnostics-linux-astra-desktop-manual-e2e",
    "gd31j27aq-recovery-keyboard-narrator-windows-manual-e2e",
    "gd31j27aq-recovery-keyboard-screenreader-astra-manual-e2e",
    "gd31j27ar-recovery-tab-cancellation-windows-manual-e2e",
    "gd31j27ar-recovery-tab-cancellation-astra-manual-e2e",
    "gd31j27as-reopen-document-session-windows-manual-e2e",
    "gd31j27as-shutdown-long-smb-scan-astra-manual-e2e",
    "gd31j27au-recovery-name-collision-windows-ntfs-manual-e2e",
    "gd31j27au-recovery-name-collision-linux-astra-manual-e2e",
    "gd31j27av-recovery-discard-integrity-windows-ntfs-manual-e2e",
    "gd31j27av-recovery-discard-integrity-astra-smb-manual-e2e",
    "gd31j27aw-recovery-partial-delete-windows-ntfs-manual-e2e",
    "gd31j27aw-recovery-partial-delete-astra-smb-manual-e2e",
    "gd31j27ax-recovery-manifest-fault-windows-ntfs-manual-e2e",
    "gd31j27ax-recovery-manifest-fault-astra-smb-manual-e2e",
    "gd31j27ay-recovery-folder-state-windows-ntfs-manual-e2e",
    "gd31j27ay-recovery-folder-state-astra-smb-manual-e2e",
    "gd31j27az-recovery-fast-selection-windows-ntfs-manual-e2e",
    "gd31j27az-recovery-fast-selection-astra-smb-manual-e2e",
    "gd31j27ba-recovery-concurrent-windows-ntfs-manual-e2e",
    "gd31j27ba-recovery-concurrent-astra-smb-manual-e2e",
    "gd31j27bb-single-stream-windows-ntfs-manual-e2e",
    "gd31j27bb-single-stream-astra-smb-manual-e2e",
    "gd31j27bc-recovery-preopen-windows-ntfs-manual-e2e",
    "gd31j27bc-recovery-preopen-astra-smb-manual-e2e",
    "gd31j27bd-native-identity-windows-ntfs-manual-e2e",
    "gd31j27bd-native-identity-astra-ext4-smb-manual-e2e",
    "gd31j27be-native-windows-fileid-manual-e2e",
    "gd31j27be-native-astra-openat2-manual-e2e",
    "gd31j27be-native-smb-identity-manual-e2e",
    "gd31j27bf-verified-staging-windows-ntfs-manual-e2e",
    "gd31j27bf-verified-staging-astra-ext4-manual-e2e",
    "gd31j27bf-verified-staging-smb-cancellation-manual-e2e",
    "gd31j27bg-private-publication-windows-ntfs-manual-e2e",
    "gd31j27bg-private-publication-astra-ext4-manual-e2e",
    "gd31j27bg-private-publication-smb-manual-e2e",
    "gd31j27bh-consumer-windows-ntfs-manual-e2e",
    "gd31j27bh-consumer-astra-ext4-manual-e2e",
    "gd31j27bh-consumer-smb-race-manual-e2e",
    "gd31j27bi-owned-consumer-windows-ntfs-manual-e2e",
    "gd31j27bi-owned-consumer-astra-ext4-manual-e2e",
    "gd31j27bi-owned-consumer-smb-delete-on-close-manual-e2e",
    "gd31j27bj-lifecycle-windows-ntfs-kill-manual-e2e",
    "gd31j27bj-lifecycle-astra-ext4-sigkill-manual-e2e",
    "gd31j27bj-lifecycle-smb-disconnect-race-manual-e2e",
    "gd31j27bj-orphan-cleanup-authorization-manual-e2e",
    "gd31j27bk-path-rollback-windows-ntfs-manual-e2e",
    "gd31j27bk-orphan-inventory-astra-ext4-manual-e2e",
    "gd31j27bk-smb-failure-no-auto-delete-manual-e2e",
    "gd31j27bl-final-orphan-windows-ntfs-manual-e2e",
    "gd31j27bl-admission-astra-ext4-manual-e2e",
    "gd31j27bl-smb-concurrent-admission-manual-e2e",
    "gd31j27bl-native-identity-delete-denial-manual-e2e",
    "gd31j27bm-capture-lock-windows-ntfs-manual-e2e",
    "gd31j27bm-capture-lock-astra-ext4-manual-e2e",
    "gd31j27bm-unsupported-smb-fail-closed-manual-e2e",
    "gd31j27bm-disk-pressure-cancel-crash-manual-e2e",
    "gd31j27bn-volume-cross-root-windows-manual-e2e",
    "gd31j27bn-volume-cross-root-astra-manual-e2e",
    "gd31j27bn-native-root-fileid-swap-manual-e2e",
    "gd31j27bn-volume-pressure-multi-process-manual-e2e",
    "gd31j27bo-windows-two-process-same-volume-manual-e2e",
    "gd31j27bo-astra-two-process-same-volume-manual-e2e",
    "gd31j27bo-crash-restart-stale-lockfile-manual-e2e",
    "gd31j27bo-cross-user-appdata-acl-manual-e2e",
    "gd31j27bo-enospc-admission-race-manual-e2e",
    "gd31j27bp-windows-full-fileid-ntfs-manual-e2e",
    "gd31j27bp-astra-statx-ext4-manual-e2e",
    "gd31j27bp-nofollow-lock-rename-race-manual-e2e",
    "gd31j27bp-linux-coordinator-private-acl-manual-e2e",
    "gd31j27bp-windows-dacl-owner-sid-manual-e2e",
    "gd31j27bq-astra-openat2-pinned-parent-manual-e2e",
    "gd31j27bq-linux-uid-mode-mount-alias-manual-e2e",
    "gd31j27bq-windows-held-parent-owner-dacl-manual-e2e",
    "gd31j27bq-windows-ace-privilege-denial-manual-e2e",
    "gd31j27bq-parent-rename-fault-injection-manual-e2e",
    "gd31j27bs-windows-ntfs-dacl-ace-manual-e2e",
    "gd31j27bs-windows-effective-token-inheritance-manual-e2e",
    "gd31j27bs-windows-reparse-owner-race-manual-e2e",
    "gd31j27bt-windows-live-tokengroups-manual-e2e",
    "gd31j27bt-windows-restricted-impersonation-manual-e2e",
    "gd31j27bt-windows-inherited-ace-mic-privilege-manual-e2e",
    "gd31j27bu-astra-root-chain-openat2-manual-e2e",
    "gd31j27bu-astra-xattr-acl-visibility-manual-e2e",
    "gd31j27bu-astra-mount-namespace-alias-manual-e2e",
    "gd31j27bu-astra-uid-permissions-cancel-manual-e2e",
    "gd31j27bv-windows-ntfs-delete-denial-manual-e2e",
    "gd31j27bv-astra-ext4-delete-denial-manual-e2e",
    "gd31j27bv-smb-concurrent-replace-denial-manual-e2e",
    "gd31j27bv-retention-disk-budget-manual-e2e",
    "gd31j27bw-astra-openat2-name-symlink-hardlink-manual-e2e",
    "gd31j27bw-astra-rename-during-held-fd-manual-e2e",
    "gd31j27bw-windows-no-native-delete-publish-manual-e2e",
    "gd31j27bw-smb-cross-host-race-manual-e2e",
    "gd31j27bx-windows-ntfs-no-replace-directory-fsync-manual-e2e",
    "gd31j27bx-astra-renameat2-unlinkat-dirfsync-manual-e2e",
    "gd31j27bx-smb-multi-host-lock-crash-manual-e2e",
    "gd31j27bx-transaction-restart-reconcile-manual-e2e",
    "gd31j27bx-eurooffice-native-saveas-integration-manual-e2e",
    "gd31j27by-windows-crash-journal-truncation-manual-e2e",
    "gd31j27by-astra-ext4-journal-fsync-powerloss-manual-e2e",
    "gd31j27by-smb-cross-host-journal-replay-manual-e2e",
    "gd31j27by-restart-artifact-identity-review-manual-e2e",
    "gd31j27by-original-worm-preservation-manual-e2e",
    "gd31j27bz-windows-journal-dir-sync-manual-e2e",
    "gd31j27bz-astra-ext4-journal-fsync-manual-e2e",
    "gd31j27bz-windows-trusted-key-rollback-manual-e2e",
    "gd31j27bz-astra-protected-key-monotonic-manual-e2e",
    "gd31j27bz-smb-crosshost-journal-consistency-manual-e2e",
    "gd31j27bz-restart-native-identity-reopen-manual-e2e",
    "gd31j27bz-powerloss-between-journal-anchor-manual-e2e",
    "gd31j27bz-original-worm-immutability-manual-e2e",
    "gd31j27ca-windows-held-file-content-flush-manual-e2e",
    "gd31j27ca-astra-linux-held-dirfd-fsync-manual-e2e",
    "gd31j27ca-windows-ntfs-fileid-reopen-manual-e2e",
    "gd31j27ca-astra-ext4-enospc-eio-interruption-manual-e2e",
    "gd31j27ca-smb-crosshost-name-swap-manual-e2e",
    "gd31j27ca-restart-truncated-journal-readback-manual-e2e",
    "gd31j27ca-windows-directory-durability-qualification-manual-e2e",
    "gd31j27ca-worm-original-untouched-manual-e2e",
    "gd31j27cb-astra-openat2-excl-no-follow-manual-e2e",
    "gd31j27cb-astra-existing-name-no-overwrite-manual-e2e",
    "gd31j27cb-astra-uid-mode-nlink-mount-manual-e2e",
    "gd31j27cb-astra-cancel-after-create-orphan-manual-e2e",
    "gd31j27cb-windows-relative-handle-create-pending-manual-e2e",
    "gd31j27cb-smb-crosshost-create-excl-manual-e2e",
    "gd31j27cb-astra-namespace-acl-lsm-ancestry-manual-e2e",
    "gd31j27cb-worm-and-path-delete-denial-manual-e2e",
    "gd31j27cc-windows-nt-relative-create-exclusive-manual-e2e",
    "gd31j27cc-windows-reparse-junction-substitution-manual-e2e",
    "gd31j27cc-windows-ntfs-owner-dacl-token-privileges-manual-e2e",
    "gd31j27cc-windows-dacl-race-after-create-manual-e2e",
    "gd31j27cc-windows-ntstatus-handle-leak-manual-e2e",
    "gd31j27cc-windows-directory-fsync-powerloss-manual-e2e",
    "gd31j27cc-smb-crosshost-create-and-delete-race-manual-e2e",
    "gd31j27cc-worm-original-preservation-manual-e2e",
    "gd31j27cd-linux-name-replacement-between-opens-manual-e2e",
    "gd31j27cd-windows-nt-share-zero-reopen-manual-e2e",
    "gd31j27cd-windows-reparse-substitution-during-open-manual-e2e",
    "gd31j27cd-linux-held-parent-during-rename-manual-e2e",
    "gd31j27cd-smb-cross-host-name-replace-manual-e2e",
    "gd31j27cd-manifest-journal-digest-mismatch-manual-e2e",
    "gd31j27cd-restart-file-id-name-binding-manual-e2e",
    "gd31j27cd-worm-original-no-delete-manual-e2e",
    "gd31j27ce-windows-exclusive-default-no-change-manual-e2e",
    "gd31j27ce-windows-readonly-sharing-observer-manual-e2e",
    "gd31j27ce-windows-add-file-traverse-parent-manual-e2e",
    "gd31j27ce-windows-reparse-ancestor-denial-manual-e2e",
    "gd31j27ce-windows-ntfs-no-write-delete-concurrency-manual-e2e",
    "gd31j27ce-held-journal-lab-manifest-reopen-manual-e2e",
    "gd31j27ce-smb-cross-host-share-and-checkpoint-manual-e2e",
    "gd31j27ce-worm-original-unchanged-manual-e2e",
    "eurooffice-cef-authenticated-caret-ime-candidates",
    "eurooffice-windows-x11-osr-or-uno-fallback",
    "eurooffice-server-2-5-10-user-coediting",
    "corporate-sso-acl-and-worm-cas",
    "smb-conflicts-and-emergency-recovery",
    "office-native-save-conflict-and-no-overwrite-copy",
    "physical-get-print-wizard-a4-a3",
    "ms-office-file-compatibility-check",
    "licensing-signing-and-offline-installer",
)


@dataclass(frozen=True)
class CommandStep:
    name: str
    command: list[str]
    project: str | None = None


def build_steps(root: Path, feed: Path, host: str,
                qt_host_binary: Path | None = None) -> list[CommandStep]:
    """Offline restore is the ONLY allowed NuGet source; never guess a remote URL."""
    _ = root  # Callers and subprocess always operate at the checked-out repo root.
    steps = [
        CommandStep("runner-self-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests", "-p", "test_office_acceptance.py"]),
        CommandStep("savecopy-synthetic-filesystem-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests", "-p", "test_savecopy_fs_probe.py"]),
        CommandStep("recovery-readonly-inventory-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_readonly_inventory_contract.py"]),
        CommandStep("recovery-diagnostics-ui-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_diagnostics_ui_contract.py"]),
        CommandStep("recovery-diagnostics-a11y-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_diagnostics_a11y_contract.py"]),
        CommandStep("recovery-tab-lifecycle-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_tab_lifecycle_contract.py"]),
        CommandStep("recovery-reopen-shutdown-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_reopen_shutdown_contract.py"]),
        CommandStep("recovery-chain-consistency-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_chain_consistency.py"]),
        CommandStep("recovery-folder-state-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_folder_state_contract.py"]),
        CommandStep("recovery-fast-selection-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_fast_selection_contract.py"]),
        CommandStep("recovery-concurrent-selection-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_concurrent_selection_contract.py"]),
        CommandStep("recovery-single-stream-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_single_stream_contract.py"]),
        CommandStep("recovery-preopen-guard-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_preopen_guard_contract.py"]),
        CommandStep("recovery-native-identity-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_native_identity_contract.py"]),
        CommandStep("recovery-native-probes-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_native_probes_contract.py"]),
        CommandStep("recovery-verified-staging-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_verified_staging_contract.py"]),
        CommandStep("recovery-private-publication-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_private_publication_contract.py"]),
        CommandStep("recovery-consumer-transfer-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_consumer_transfer_contract.py"]),
        CommandStep("recovery-owned-consumer-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_owned_consumer_contract.py"]),
        CommandStep("recovery-lifecycle-audit-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_lifecycle_audit_contract.py"]),
        CommandStep("recovery-orphan-inventory-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_orphan_inventory_contract.py"]),
        CommandStep("recovery-orphan-budget-native-delete-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_orphan_budget_native_delete_contract.py"]),
        CommandStep("recovery-capture-volume-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_capture_volume_contract.py"]),
        CommandStep("recovery-volume-identity-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_volume_identity_contract.py"]),
        CommandStep("recovery-shared-volume-coordinator-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_shared_volume_coordinator_contract.py"]),
        CommandStep("recovery-native-handle-probe-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_native_handle_probe_contract.py"]),
        CommandStep("recovery-pinned-parent-security-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_pinned_parent_security_contract.py"]),
        CommandStep("recovery-release-readiness-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_release_readiness.py"]),
        CommandStep("recovery-windows-acl-risk-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_windows_acl_risk_contract.py"]),
        CommandStep("recovery-windows-token-groups-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_windows_token_groups_contract.py"]),
        CommandStep("recovery-linux-anchored-directory-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_linux_anchored_directory_contract.py"]),
        CommandStep("recovery-pair-mutation-denial-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_pair_mutation_denial_contract.py"]),
        CommandStep("recovery-linux-named-pair-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_linux_named_pair_contract.py"]),
        CommandStep("recovery-native-mutation-sequence-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_native_mutation_sequence_contract.py"]),
        CommandStep("recovery-crash-journal-reconcile-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_crash_journal_reconciliation_contract.py"]),
        CommandStep("recovery-durable-checkpoint-blocks-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_durable_checkpoint_blocks_contract.py"]),
        CommandStep("recovery-held-journal-native-lab-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_held_journal_native_lab_contract.py"]),
        CommandStep("recovery-linux-scratch-factory-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_linux_scratch_factory_contract.py"]),
        CommandStep("recovery-windows-nt-rooted-scratch-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_windows_nt_rooted_scratch_contract.py"]),
        CommandStep("recovery-scratch-name-pair-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_scratch_name_pair_contract.py"]),
        CommandStep("recovery-ce-windows-share-held-manifest-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_recovery_ce_windows_share_held_manifest_contract.py"]),
        CommandStep("office-native-save-receipt-contract-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_lo_save_receipt_contract.py"]),
        CommandStep("windows-named-pipe-denial-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/windows-saveas-denial",
                     "-p", "test_windows_named_pipe_denial_source.py"]),
        CommandStep("native-probe-mock-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop", "-p", "test_mock_host.py"]),
        CommandStep("cef-lab-offline-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_cef_lab_gate.py"]),
        CommandStep("cef-empty-view-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_empty_view_contract.py"]),
        CommandStep("qt-display-evidence-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop",
                     "-p", "test_qt_display_evidence.py"]),
        CommandStep("native-event-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_event_contract.py"]),
        CommandStep("qt-ipc-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop",
                     "-p", "test_qt_ipc_contract.py"]),
        CommandStep("native-avalonia-tab-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "scripts/tests", "-p", "test_native_tab_shell_contract.py"]),
        CommandStep("native-session-dispatch-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "scripts/tests", "-p", "test_native_session_dispatch_contract.py"]),
        CommandStep("native-frame-shell-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "scripts/tests", "-p", "test_native_frame_shell_contract.py"]),
        CommandStep("native-saveas-manual-triparty-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_manual_triparty_devnull.py"]),
        CommandStep("native-saveas-manual-triparty-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_manual_triparty_contract.py"]),
        CommandStep("native-saveas-three-party-trace-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_three_party_offline_trace.py"]),
        CommandStep("native-saveas-three-party-trace-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_three_party_offline_trace_contract.py"]),
        CommandStep("native-saveas-disposable-vm-preopen-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_disposable_vm_preopen.py"]),
        CommandStep("native-saveas-disposable-vm-preopen-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_disposable_vm_preopen_contract.py"]),
        CommandStep("native-saveas-fd-origin-namespace-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_fd_origin_namespace.py"]),
        CommandStep("native-saveas-fd-origin-namespace-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_fd_origin_namespace_contract.py"]),
        CommandStep("native-saveas-proc-maps-elf-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_proc_maps_elf_collector.py"]),
        CommandStep("native-saveas-proc-maps-elf-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_proc_maps_elf_contract.py"]),
        CommandStep("native-saveas-offline-vm-evidence-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_offline_vm_evidence.py"]),
        CommandStep("native-saveas-offline-vm-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_offline_vm_evidence_contract.py"]),
        CommandStep("native-saveas-offline-sbom-evidence-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_offline_sbom_evidence.py"]),
        CommandStep("native-saveas-offline-sbom-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_offline_sbom_contract.py"]),
        CommandStep("native-saveas-dependency-unit-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_dependency_unit_pins.py"]),
        CommandStep("native-saveas-dependency-unit-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_dependency_unit_pins_contract.py"]),
        CommandStep("native-saveas-launch-provenance-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_launch_provenance.py"]),
        CommandStep("native-saveas-launch-provenance-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_launch_provenance_contract.py"]),
        CommandStep("native-saveas-fd3-challenge-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_fd3_challenge.py"]),
        CommandStep("native-saveas-fd3-challenge-flow-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_fd3_challenge_flow.py"]),
        CommandStep("native-saveas-fd3-challenge-contract-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_fd3_challenge_contract.py"]),
        CommandStep("native-saveas-socket-peerpidfd-kernel-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_socket_peerpidfd.py"]),
        CommandStep("native-saveas-socket-peerpidfd-broker-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_socket_peerpidfd_broker.py"]),
        CommandStep("native-saveas-socket-peerpidfd-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_socket_peerpidfd_contract.py"]),
        CommandStep("native-saveas-worker-pidfd-handoff-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_worker_pidfd_handoff.py"]),
        CommandStep("native-saveas-worker-pidfd-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_worker_pidfd_handoff_contract.py"]),
        CommandStep("native-saveas-broker-pidfd-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_pidfd_provenance.py"]),
        CommandStep("native-saveas-broker-pidfd-sender-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_root_sender_pidfd_guard.py"]),
        CommandStep("native-saveas-broker-pidfd-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_pidfd_source_contract.py"]),
        CommandStep("native-saveas-root-supervisor-audit-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_root_supervisor_audit.py"]),
        CommandStep("native-saveas-root-supervisor-audit-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_root_supervisor_audit_contract.py"]),
        CommandStep("native-saveas-root-supervisor-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_root_supervisor.py"]),
        CommandStep("native-saveas-root-supervisor-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_root_supervisor_contract.py"]),
        CommandStep("native-saveas-three-party-bridge-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_three_party_bridge.py"]),
        CommandStep("native-saveas-three-party-bridge-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_three_party_bridge_contract.py"]),
        CommandStep("native-saveas-fd-provisioning-linux-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_fd_provisioning.py"]),
        CommandStep("native-saveas-fd-provisioning-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_fd_provisioning_contract.py"]),
        CommandStep("native-saveas-worker-anonymous-snapshot-linux-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_worker_snapshot.py"]),
        CommandStep("native-saveas-worker-snapshot-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_worker_snapshot_contract.py"]),
        CommandStep("native-saveas-post-uid-dialer-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_worker_dialer.py"]),
        CommandStep("native-saveas-post-uid-dialer-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_worker_dialer_contract.py"]),
        CommandStep("native-saveas-post-uid-broker-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_broker_listener.py"]),
        CommandStep("native-saveas-post-uid-broker-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_broker_listener_contract.py"]),
        CommandStep("native-saveas-supervisor-handoff-observer-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_supervisor_handoff.py"]),
        CommandStep("native-saveas-supervisor-broker-guard-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_supervisor_broker_guard.py"]),
        CommandStep("native-saveas-supervisor-handoff-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_supervisor_handoff_contract.py"]),
        CommandStep("native-saveas-kernel-sandbox-observer-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_sandbox_observer.py"]),
        CommandStep("native-saveas-kernel-sandbox-observer-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_sandbox_observer_contract.py"]),
        CommandStep("native-saveas-fd-broker-protocol-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_fd_broker.py"]),
        CommandStep("native-saveas-broker-profile-contract-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_broker_profile.py"]),
        CommandStep("native-saveas-fd-broker-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_fd_broker_contract.py"]),
        CommandStep("native-saveas-fd-worker-offline-protocol-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_fd_worker.py"]),
        CommandStep("native-saveas-fd-worker-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_fd_worker_contract.py"]),
        CommandStep("native-saveas-unlinked-snapshot-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_unlinked_snapshot_contract.py"]),
        CommandStep("native-saveas-unlinked-snapshot-linux-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_unlinked_snapshot_linux.py"]),
        CommandStep("native-saveas-fd-bound-link-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_fd_bound_link_contract.py"]),
        CommandStep("native-saveas-fd-bound-link-linux-regression-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_fd_bound_link_linux.py"]),
        CommandStep("native-saveas-offline-acceptance-evidence-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_acceptance_gate.py"]),
        CommandStep("native-saveas-offline-acceptance-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_acceptance_gate_contract.py"]),
        CommandStep("native-saveas-complete-chain-reconciliation-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_chain_reconciliation.py"]),
        CommandStep("native-saveas-durable-policy-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_durable_policy_contract.py"]),
        CommandStep("native-saveas-orphan-read-only-inventory-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_orphan_inventory.py"]),
        CommandStep("pinned-saveas-event-budget-lifecycle-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_event_budget_contract.py"]),
        CommandStep("pinned-saveas-state-negative-matrix-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_pinned_saveas_state_model.py"]),
        CommandStep("pinned-saveas-state-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_pinned_saveas_state_source_contract.py"]),
        CommandStep("pinned-native-v2-supervisor-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_pinned_v2_supervisor.py"]),
        CommandStep("pinned-native-v2-supervisor-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_pinned_v2_supervisor_contract.py"]),
        CommandStep("pinned-native-saveas-event-v2-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_pinned_saveas_event_v2.py"]),
        CommandStep("pinned-native-saveas-event-v2-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_pinned_saveas_event_v2_contract.py"]),
        CommandStep("pinned-native-event-v2-ooxml-join-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_pinned_event_ooxml_join.py"]),
        CommandStep("native-saveas-protocol-reconciliation-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_protocol_reconciliation.py"]),
        CommandStep("native-saveas-protocol-reconciliation-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_protocol_reconciliation_contract.py"]),
        CommandStep("native-ooxml-supervisor-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_ooxml_supervisor.py"]),
        CommandStep("native-ooxml-supervisor-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_ooxml_supervisor_contract.py"]),
        CommandStep("native-ooxml-join-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_ooxml_join.py"]),
        CommandStep("native-ooxml-join-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_ooxml_join_contract.py"]),
        CommandStep("cef-native-saveas-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_contract.py"]),
        CommandStep("synthetic-ooxml-roundtrip-verifier-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_synthetic_roundtrip.py"]),
        CommandStep("cef-native-ready-overlay-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_ready_bridge_overlay.py"]),
        CommandStep("cef-native-ready-bridge-source-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_ready_bridge_contract.py"]),
        CommandStep("cef-native-conversion-evidence-source-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_conversion_evidence_contract.py"]),
        CommandStep("cef-native-binding-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_binding_contract.py"]),
        CommandStep("cef-wayland-osr-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_osr_capture_contract.py"]),
        CommandStep("cef-osr-stream-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "scripts/tests", "-p", "test_osr_stream_contract.py"]),
        CommandStep("cef-osr-input-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_osr_input_contract.py"]),
        CommandStep("cef-osr-click-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_osr_click_contract.py"]),
        CommandStep("cef-osr-keyboard-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_osr_keyboard_contract.py"]),
        CommandStep("cef-osr-ime-commit-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_osr_ime_commit_contract.py"]),
        CommandStep("eurooffice-stacked-contract-integrity-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_eurooffice_stacked_contract_integrity.py"]),
        CommandStep("avalonia-ime-caret-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_avalonia_ime_caret_contract.py"]),
        CommandStep("solution-restore",
                    ["dotnet", "restore", "GetDocument.slnx", "--source", str(feed), "-p:NuGetAudit=false"]),
        CommandStep("solution-build",
                    ["dotnet", "build", "GetDocument.slnx", "-c", "Release", "--no-restore"]),
    ]
    if qt_host_binary is not None:
        # This is a REAL local compiled Qt process test, never a mock. It
        # advertises zero file permissions and does not load CEF or documents.
        steps.insert(2, CommandStep("native-qt-headless-smoke", [
            sys.executable, "probes/eurooffice-desktop/test_qt_host.py",
            str(qt_host_binary)
        ]))
        steps.insert(3, CommandStep("native-qt-display-offscreen", [
            sys.executable, "probes/eurooffice-desktop/test_qt_display.py",
            str(qt_host_binary)
        ]))
        steps.insert(4, CommandStep("native-qt-tab-ipc", [
            sys.executable, "probes/eurooffice-desktop/test_qt_ipc.py",
            str(qt_host_binary)
        ]))
    for label, project in TEST_PROJECTS.items():
        steps.append(CommandStep(
            "test-" + label,
            ["dotnet", "test", project, "-c", "Release", "--no-restore",
             "--minimum-expected-tests", "1"],
            project=project))
    if host == "Windows":
        worker = "src/GetDocument.PrintWindows/GetDocument.PrintWindows.csproj"
        win_denial = "probes/windows-saveas-denial/GetDocWindowsPipeDenialLab.csproj"
        steps.extend([
            CommandStep("windows-denial-probe-restore",
                        ["dotnet", "restore", win_denial, "--source", str(feed),
                         "-p:NuGetAudit=false"]),
            CommandStep("windows-denial-probe-build",
                        ["dotnet", "build", win_denial, "-c", "Release",
                         "--no-restore"]),
            CommandStep("windows-print-worker-restore",
                        ["dotnet", "restore", worker, "--source", str(feed), "-p:NuGetAudit=false"]),
            CommandStep("windows-print-worker-build",
                        ["dotnet", "build", worker, "-c", "Release", "--no-restore"]),
            CommandStep("windows-legacy-solution-restore",
                        ["dotnet", "restore", "GetPdf.slnx", "--source", str(feed), "-p:NuGetAudit=false"]),
            CommandStep("windows-legacy-solution-build",
                        ["dotnet", "build", "GetPdf.slnx", "-c", "Release", "--no-restore"]),
            CommandStep("test-legacy-pdf",
                        ["dotnet", "test", "tests/GetPdf.Tests/GetPdf.Tests.csproj",
                         "-c", "Release", "--no-restore", "--minimum-expected-tests", "1"],
                        project="tests/GetPdf.Tests/GetPdf.Tests.csproj")
        ])
    return steps


def run_steps(steps: list[CommandStep], invoke) -> tuple[list[dict], bool]:
    """Stop on the FIRST failing step. Never claim unexecuted steps passed."""
    if not steps:
        return [], False
    results = []
    failed = False
    for step in steps:
        if failed:
            results.append({"name": step.name, "status": "not-run", "exitCode": None})
            continue
        started = time.monotonic()
        try:
            code = int(invoke(step))
        except (OSError, subprocess.TimeoutExpired, ValueError):
            code = 127
        passed = code == 0
        results.append({
            "name": step.name,
            "status": "passed" if passed else "failed",
            "exitCode": code,
            "durationSeconds": round(time.monotonic() - started, 2),
        })
        if not passed:
            failed = True
    return results, not failed


def check_test_runner(root: Path) -> None:
    config = json.loads((root / "global.json").read_text(encoding="utf-8"))
    if config.get("test", {}).get("runner") != "Microsoft.Testing.Platform":
        raise ValueError("global.json must explicitly select Microsoft.Testing.Platform.")


def check_python_syntax(source: str) -> None:
    ast.parse(source, filename="lo_editor.py", mode="exec")


def compare_feature_catalogs(csharp: str, python: str) -> list[str]:
    catalog = re.findall(r'new\("((?:writer|calc|impress)-[a-z-]+)"', csharp)
    bridge = re.findall(
        r'^    "((?:writer|calc|impress)-[a-z-]+)": \("(?:writer|calc|impress)", (?:True|False)\),?$',
        python, flags=re.MULTILINE)
    if not catalog or len(catalog) != len(set(catalog)) or catalog != bridge:
        raise ValueError("OfficeFeatureCatalog.cs and lo_editor.py whitelist disagree.")
    return catalog


def source_preflight(root: Path) -> dict:
    if not (root / "GetDocument.slnx").is_file():
        raise FileNotFoundError("GetDocument.slnx is missing.")
    check_test_runner(root)
    office = root / "src/GetDocument.Office/LibreOffice"
    bridge = (office / "lo_editor.py").read_text(encoding="utf-8")
    check_python_syntax(bridge)
    check_python_syntax((office / "lo_bridge.py").read_text(encoding="utf-8"))
    catalog = (office / "OfficeFeatureCatalog.cs").read_text(encoding="utf-8")
    ids = compare_feature_catalogs(catalog, bridge)
    for project in TEST_PROJECTS.values():
        if not (root / project).is_file():
            raise FileNotFoundError("Required test project is missing: " + project)
    return {"microsoftTestingPlatform": True, "pythonBridgeSyntax": True,
            "localFeatureCatalogMatch": True, "featureCount": len(ids)}


def new_report(revision: str, host: str, stage: str) -> dict:
    return {
        "schemaVersion": 1,
        "stage": stage,
        "revision": revision,
        "host": host,
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "status": "blocked",
        "automatedChecksPassed": False,
        # Never make this True by a successful dotnet test: real editors, ACL and
        # hardware printing require independent human acceptance evidence.
        "releaseReady": False,
        "manualAcceptance": {name: "not-run" for name in MANUAL_GATES},
        "preflight": {},
        "steps": [],
    }


def write_report(path: Path, report: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        with temporary.open("x", encoding="utf-8") as output:
            json.dump(report, output, ensure_ascii=False, indent=2)
            output.write("\n")
            output.flush()
            os.fsync(output.fileno())
        # A hard-link publishes a complete report atomically and FAILS if the
        # named evidence file already exists. Do not use os.replace here:
        # overwriting an earlier result would defeat the acceptance audit.
        os.link(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def git_revision(root: Path) -> str:
    result = subprocess.run(
        ["git", "rev-parse", "--verify", "HEAD"], cwd=root, text=True,
        capture_output=True, check=True, timeout=10)
    revision = result.stdout.strip()
    if not re.fullmatch(r"[0-9a-f]{40}", revision):
        raise ValueError("No valid Git revision for this acceptance run.")
    dirty = subprocess.run(
        ["git", "status", "--porcelain", "--untracked-files=all"], cwd=root,
        text=True, capture_output=True, check=True, timeout=15)
    if dirty.stdout.strip():
        # A Git SHA cannot attest to modified, uncommitted code. Reports and
        # build outputs live in .gitignored artifacts/ and do not trip this gate.
        raise RuntimeError("The Git worktree contains uncommitted changes; "
                           "acceptance requires an exact committed revision.")
    return revision


def ensure_dotnet10() -> str:
    if not shutil.which("dotnet"):
        raise RuntimeError(".NET 10 SDK is not installed.")
    result = subprocess.run(["dotnet", "--version"], text=True,
                            capture_output=True, check=True, timeout=20)
    version = result.stdout.strip()
    if not re.fullmatch(r"10\.\d+\.\d+", version):
        raise RuntimeError("Expected .NET 10 SDK, found " + version)
    return version


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Local offline GET Documents acceptance.")
    parser.add_argument("--feed", type=Path,
                        help="Existing APPROVED offline NuGet directory (required for checks).")
    parser.add_argument("--plan", action="store_true", help="Inspect only; do not build/test.")
    parser.add_argument("--qt-host-binary", type=Path,
                        help="Locally compiled Qt host; enables REAL headless handshake smoke test.")
    parser.add_argument("--report", type=Path,
                        help="Output JSON path; default artifacts/local-acceptance/<unique>.json")
    args = parser.parse_args(argv)
    root = REPO_ROOT
    host = platform.system()
    report = new_report("unknown", host, "GD-31I")
    path = args.report or root / "artifacts" / "local-acceptance" / (
        datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S") + "-" +
        uuid.uuid4().hex[:10] + ".json")
    result_code = 2
    try:
        report["revision"] = git_revision(root)
        report["preflight"] = source_preflight(root)
        if host not in ("Windows", "Linux"):
            raise RuntimeError("Only Windows and Linux test hosts are supported.")

        qt_binary = args.qt_host_binary.expanduser().resolve() if args.qt_host_binary else None
        if qt_binary is not None and not args.plan and not qt_binary.is_file():
            raise RuntimeError("Compiled local Qt host binary was not found.")

        if args.plan:
            report["status"] = "plan-only"
            report["steps"] = [
                {"name": item.name, "status": "not-run", "command": item.command}
                for item in build_steps(root, Path("<approved-offline-feed>"), host,
                                        qt_host_binary=qt_binary)
            ]
            result_code = 0
        else:
            if args.feed is None:
                raise RuntimeError("--feed is required; network restore is forbidden.")
            feed = args.feed.expanduser().resolve()
            if not feed.is_dir():
                raise RuntimeError("Approved local NuGet feed directory does not exist.")
            report["dotnetSdk"] = ensure_dotnet10()
            steps = build_steps(root, feed, host, qt_host_binary=qt_binary)
            environment = os.environ.copy()
            environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
            environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
            environment["DOTNET_NOLOGO"] = "1"
            environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true"

            def invoke(step: CommandStep) -> int:
                print("[GD-31I]", step.name, flush=True)
                # Explicit argument array, no shell interpolation and no online
                # restore; logs remain in the current terminal, NOT in report.
                result = subprocess.run(step.command, cwd=root, env=environment,
                                        check=False, timeout=1800)
                return result.returncode

            report["steps"], passed = run_steps(steps, invoke)
            report["automatedChecksPassed"] = passed
            report["status"] = "automated-checks-passed" if passed else "failed"
            result_code = 0 if passed else 1
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError,
            subprocess.TimeoutExpired, json.JSONDecodeError, SyntaxError) as exc:
        report["status"] = "blocked"
        # No credentials, URLs or process output are copied into the report.
        report["blockingReason"] = type(exc).__name__ + ": " + str(exc)[:240]
        print("BLOCKED:", report["blockingReason"], file=sys.stderr)
    finally:
        write_report(path, report)
        print("GD-31I report:", path)
        print("Automatic:", report["status"])
        print("RELEASE APPROVED: NO — manual/hardware/backend gates remain NOT RUN.")
    return result_code


if __name__ == "__main__":
    raise SystemExit(main())
