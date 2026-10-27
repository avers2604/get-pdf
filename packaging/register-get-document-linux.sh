#!/usr/bin/env bash
# Opt-in desktop entry; does not change MIME defaults or system configuration.
set -euo pipefail
if (( $# != 1 )); then
  echo 'Usage: register-get-document-linux.sh /absolute/path/to/GetDocument.Desktop' >&2
  exit 2
fi
exe="$1"
if [[ "$exe" != /* || ! -f "$exe" || ! -x "$exe" ]]; then
  echo 'An existing, executable absolute path is required.' >&2
  exit 2
fi
# Desktop Entry Exec has special field codes and quoting semantics: reject
# unsafe characters rather than guessing an escaping scheme.
for forbidden in '"' '\' '%' '$' $'\x60' $'\n' $'\r'; do
  if [[ "$exe" == *"$forbidden"* ]]; then
    echo 'Executable path has characters unsuitable for a desktop Exec entry.' >&2
    exit 2
  fi
done
apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
mkdir -p -- "$apps"
tmp="$(mktemp "$apps/.get-document.XXXXXX")"
trap 'rm -f -- "$tmp"' EXIT
cat > "$tmp" <<DESKTOP
[Desktop Entry]
Type=Application
Name=ГЭТ Документ
Comment=Документы ГЭТ в едином окне
Exec="$exe" %F
Terminal=false
StartupNotify=true
Categories=Office;Viewer;
MimeType=application/pdf;application/vnd.openxmlformats-officedocument.wordprocessingml.document;application/vnd.openxmlformats-officedocument.spreadsheetml.sheet;application/vnd.openxmlformats-officedocument.presentationml.presentation;image/jpeg;image/png;
DESKTOP
chmod 0644 "$tmp"
mv -f -- "$tmp" "$apps/get-document.desktop"
# Do NOT call xdg-mime default or write mimeapps.list here.
echo "Registered GetDocument desktop entry: $apps/get-document.desktop"
