#!/usr/bin/env bash
set -euo pipefail
script="$(dirname "$0")/register-get-document-linux.sh"
tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT
mkdir -p "$tmp/bin" "$tmp/data"
exe="$tmp/bin/GET Document"
printf '#!/bin/sh\nexit 0\n' > "$exe"
chmod +x "$exe"
export XDG_DATA_HOME="$tmp/data"
bash "$script" "$exe"
entry="$tmp/data/applications/get-document.desktop"
test -f "$entry"
grep -Fqx 'Name=ГЭТ Документ' "$entry"
grep -Fqx "Exec=\"$exe\" %F" "$entry"
grep -Fq 'application/pdf;' "$entry"
test ! -e "$tmp/data/applications/mimeapps.list"
if bash "$script" relative-path > /dev/null 2>&1; then
  echo 'FAIL: accepted relative executable path' >&2
  exit 1
fi
bad="$tmp/bin/evil%F"
cp "$exe" "$bad"
chmod +x "$bad"
if bash "$script" "$bad" > /dev/null 2>&1; then
  echo 'FAIL: accepted invalid executable path' >&2
  exit 1
fi
# Registering never modifies the OS default handler.
grep -Fqx "Exec=\"$exe\" %F" "$entry"
echo 'PASS: opt-in Linux desktop entry is valid and preserves MIME defaults.'
