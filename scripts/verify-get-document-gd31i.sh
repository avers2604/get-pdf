#!/usr/bin/env bash
# GD-31I — offline local verification on Linux/Astra. Never runs CI or downloads tools.
set -euo pipefail
if [[ $# -lt 1 || -z "$1" ]]; then
  echo "Usage: bash scripts/verify-get-document-gd31i.sh /approved/offline/nuget-feed [report.json]" >&2
  exit 2
fi
if ! command -v python3 >/dev/null 2>&1; then
  echo "ERROR: Python 3.10+ is required for the QA acceptance tool, not the GET runtime." >&2
  exit 127
fi
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
feed="$1"
shift
cd "$root"
if [[ $# -gt 0 ]]; then
  exec python3 scripts/office_acceptance.py --feed "$feed" --report "$1"
else
  exec python3 scripts/office_acceptance.py --feed "$feed"
fi
