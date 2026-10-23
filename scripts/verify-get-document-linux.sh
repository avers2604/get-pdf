#!/usr/bin/env bash
# Offline-first project acceptance runner for Linux. No GitHub Actions.
set -euo pipefail
cd "$(dirname "$0")/.."
if ! command -v dotnet >/dev/null 2>&1; then
  echo "ERROR: .NET 10 SDK is required locally; install it before testing." >&2
  exit 127
fi
major="$(dotnet --version | cut -d. -f1)"
if [ "$major" -lt 10 ]; then
  echo "ERROR: .NET 10 SDK is required; found $(dotnet --version)." >&2
  exit 1
fi
dotnet restore GetDocument.slnx
dotnet build GetDocument.slnx -c Release --no-restore
dotnet test tests/GetDocument.Core.Tests/GetDocument.Core.Tests.csproj -c Release --no-restore
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore
dotnet test tests/GetDocument.Images.Tests/GetDocument.Images.Tests.csproj -c Release --no-restore
echo "PASS: core and Office test projects have completed locally."
echo "NEXT: run Avalonia, XLS/XLSB fixtures and SMB/CIFS UI acceptance manually."
