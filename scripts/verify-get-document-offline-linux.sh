#!/usr/bin/env bash
# GD-23: strictly offline validation of GET Document (no GitHub Actions).
# Usage: bash scripts/verify-get-document-offline-linux.sh /srv/get/nuget-feed
set -euo pipefail
cd "$(dirname "$0")/.."
feed="${1:-${GETDOC_OFFLINE_NUGET_FEED:-}}"
if [[ -z "$feed" || "$feed" != /* || ! -d "$feed" ]]; then
  echo "ERROR: provide an existing ABSOLUTE local NuGet feed directory." >&2
  exit 2
fi
if [[ "$feed" == *"://"* ]] || [[ -L "$feed" ]]; then
  echo "ERROR: network NuGet sources and symlink feeds are prohibited." >&2
  exit 2
fi
# A path beginning with '/' can still be NFS, CIFS, or another network mount.
# Astra/ALT/RED Linux provide util-linux findmnt in standard installations.
if ! command -v findmnt >/dev/null 2>&1; then
  echo "ERROR: findmnt is required to confirm the package feed mount type." >&2
  exit 2
fi
filesystem="$(findmnt -T "$feed" --noheadings --output FSTYPE | head -n 1)"
if [[ -z "$filesystem" ]]; then
  echo "ERROR: cannot determine the offline feed filesystem." >&2
  exit 2
fi
case "$filesystem" in
  nfs*|cifs|smb*|sshfs|fuse.*|davfs*|9p|ceph|glusterfs|lustre)
    echo "ERROR: NuGet feed resides on a potentially network-backed mount ($filesystem)." >&2
    exit 2 ;;
esac
if ! command -v dotnet >/dev/null 2>&1; then
  echo "ERROR: local .NET 10 SDK not installed." >&2
  exit 127
fi
major="$(dotnet --version | cut -d. -f1)"
if [[ "$major" -lt 10 ]]; then
  echo "ERROR: .NET 10 SDK required." >&2
  exit 1
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_NOLOGO=1
# Keep packaging/desktop registration checks in the same offline gate.
bash packaging/test-register-get-document-linux.sh
# '--source' replaces the configured feed set; NuGetAudit is disabled here
# because the sealed network must not call the public vulnerability endpoint.
# Admins verify the signed/pinned package bundle separately before rollout.
dotnet restore GetDocument.slnx --source "$feed" -p:NuGetAudit=false
dotnet build GetDocument.slnx -c Release --no-restore
dotnet test --project tests/GetDocument.Core.Tests/GetDocument.Core.Tests.csproj -c Release --no-restore
dotnet test --project tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore
dotnet test --project tests/GetDocument.Printing.Tests/GetDocument.Printing.Tests.csproj -c Release --no-restore
dotnet test --project tests/GetDocument.Images.Tests/GetDocument.Images.Tests.csproj -c Release --no-restore
dotnet test --project tests/GetDocument.Revisions.Tests/GetDocument.Revisions.Tests.csproj -c Release --no-restore
dotnet test --project tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj -c Release --no-restore
dotnet test --project tests/GetDocument.WopiGateway.Tests/GetDocument.WopiGateway.Tests.csproj -c Release --no-restore
echo "PASS: offline restore/build/tests. Real hardware printing not tested."
