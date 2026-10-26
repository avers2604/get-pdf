#!/usr/bin/env bash
# GD-22: local CUPS simulator acceptance without a printer or external CUPS server.
# All test commands are generated in a temporary private directory and run
# via the actual GetDocument.Desktop LocalPrintBroker process transport.
set -euo pipefail
cd "$(dirname "$0")/.."
if ! command -v dotnet >/dev/null 2>&1; then
  echo "ERROR: .NET 10 SDK required for GD-22 CUPS process smoke tests." >&2
  exit 127
fi
major="$(dotnet --version | cut -d. -f1)"
if [ "$major" -lt 10 ]; then
  echo "ERROR: .NET 10 SDK required, found $(dotnet --version)." >&2
  exit 1
fi
dotnet restore tests/GetDocument.Printing.Tests/GetDocument.Printing.Tests.csproj
dotnet test tests/GetDocument.Printing.Tests/GetDocument.Printing.Tests.csproj \
  -c Release --no-restore
echo "PASS: local .NET print transport integration tests. No physical printer used."
