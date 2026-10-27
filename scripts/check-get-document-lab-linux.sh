#!/usr/bin/env bash
# GD-27: fail-closed preflight for an isolated Astra/ALT/RED Linux release lab.
# Does not install software, download packages or touch real printers.
set -euo pipefail
cd "$(dirname "$0")/.."

feed="${1:-${GETDOC_OFFLINE_NUGET_FEED:-}}"
fail=0
check() {
  local label="$1" status="$2"
  if [[ "$status" == "0" ]]; then
    printf 'PASS %-30s\n' "$label"
  else
    printf 'FAIL %-30s\n' "$label" >&2
    fail=1
  fi
}
if [[ -n "$feed" && "$feed" == /* && -d "$feed" && ! -L "$feed" ]]; then
  if command -v findmnt >/dev/null 2>&1; then
    fs="$(findmnt -T "$feed" --noheadings --output FSTYPE | head -n 1 || true)"
    case "$fs" in
      ""|nfs*|cifs|smb*|sshfs|fuse.*|davfs*|9p|ceph|glusterfs|lustre)
        check "Offline NuGet local mount" 1 ;;
      *) check "Offline NuGet local mount" 0 ;;
    esac
  else
    check "findmnt mount inspection" 1
  fi
else
  check "Offline NuGet directory" 1
fi

if command -v dotnet >/dev/null 2>&1; then
  sdk="$(dotnet --version 2>/dev/null || true)"
  major="${sdk%%.*}"
  if [[ "$major" =~ ^[0-9]+$ ]] && (( major >= 10 )); then
    check ".NET 10+ SDK ($sdk)" 0
  else
    check ".NET 10+ SDK ($sdk)" 1
  fi
else
  check ".NET 10+ SDK installed" 1
fi

for path in global.json GetDocument.slnx GetPdf.slnx \
    tests/GetDocument.Printing.Tests/GetDocument.Printing.Tests.csproj \
    tools/GetDocument.ReleaseGate/GetDocument.ReleaseGate.csproj \
    scripts/verify-get-document-release-linux.sh \
    packaging/test-register-get-document-linux.sh; do
  if [[ -f "$path" ]]; then
    check "$path" 0
  else
    check "$path" 1
  fi
done

if command -v sha256sum >/dev/null 2>&1; then
  printf '\nReference source-file checksums (not test evidence):\n'
  sha256sum global.json GetDocument.slnx 2>/dev/null || fail=1
fi
printf '\nHardware tests are NOT executed by preflight.\n'
if [[ "$fail" -ne 0 ]]; then
  printf 'BLOCKED: complete offline prerequisites before the release laboratory.\n' >&2
  exit 1
fi
printf 'PREFLIGHT PASS: proceed to offline build, MSTest and manual printer acceptance.\n'
