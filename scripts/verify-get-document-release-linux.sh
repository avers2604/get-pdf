#!/usr/bin/env bash
# GD-25: strict release candidate verification in a physically isolated Linux lab.
# Requires a previously approved local NuGet feed; never uploads reports.
set -euo pipefail
cd "$(dirname "$0")/.."
feed="${1:-${GETDOC_OFFLINE_NUGET_FEED:-}}"
if [[ -z "$feed" ]]; then
  echo "Usage: bash scripts/verify-get-document-release-linux.sh /local/nuget-feed" >&2
  exit 2
fi
bash scripts/verify-get-document-offline-linux.sh "$feed"

dotnet restore tools/GetDocument.ReleaseGate/GetDocument.ReleaseGate.csproj \
  --source "$feed" -p:NuGetAudit=false
dotnet build tools/GetDocument.ReleaseGate/GetDocument.ReleaseGate.csproj \
  -c Release --no-restore

# TRX may include user documents and local paths: store reports privately,
# outside shared source checkout; chmod limits access to the current OS user.
state_root="${XDG_STATE_HOME:-$HOME/.local/state}/GET-Document/ReleaseReports"
mkdir -p "$state_root"
chmod 700 "$state_root"
reports="$(mktemp -d "$state_root/gd25-linux-XXXXXXXX")"
chmod 700 "$reports"
echo "Local TRX reports: $reports"
for suite in Core Office Printing Images Revisions Collaboration WopiGateway; do
  project="tests/GetDocument.${suite}.Tests/GetDocument.${suite}.Tests.csproj"
  # MSTest.Sdk 4.5 uses Microsoft.Testing.Platform, not the old --logger trx.
  dotnet test "$project" -c Release --no-restore -- \
    --report-trx --report-trx-filename "${suite}.trx" \
    --results-directory "$reports"
done
dotnet run --project tools/GetDocument.ReleaseGate/GetDocument.ReleaseGate.csproj \
  -c Release --no-build --no-restore -- "$reports" linux
echo "PASS: GD-25 automated release gate. Physical print acceptance remains mandatory."
