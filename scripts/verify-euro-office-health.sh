#!/usr/bin/env bash
set -euo pipefail

# GЭТ offline smoke gate. No insecure TLS, no redirects, no public DNS required.
# Usage:
#   GET_EUROOFFICE_ORIGIN=https://office.intra.example/ \
#   GET_CORPORATE_CA=/path/to/enterprise-ca.pem \
#   bash scripts/verify-euro-office-health.sh

origin="${GET_EUROOFFICE_ORIGIN:-}"
ca="${GET_CORPORATE_CA:-}"

if [[ ! "$origin" =~ ^https://[a-zA-Z0-9.-]+(:[0-9]{1,5})?/$ ]]; then
  echo "ERROR: GET_EUROOFFICE_ORIGIN must be an explicit HTTPS origin ending in /" >&2
  exit 2
fi
if [[ ! -r "$ca" ]]; then
  echo "ERROR: GET_CORPORATE_CA must point to a readable trusted CA bundle" >&2
  exit 2
fi
if ! command -v curl >/dev/null 2>&1; then
  echo "ERROR: curl is required" >&2
  exit 2
fi

result="$(curl --fail --silent --show-error --proto '=https' \
  --tlsv1.2 --connect-timeout 5 --max-time 15 --cacert "$ca" \
  "${origin}healthcheck")"

if [[ "$result" != "true" ]]; then
  echo "ERROR: Euro-Office /healthcheck did not return true" >&2
  exit 1
fi

echo "PASS: Euro-Office HTTPS healthcheck. Not a document or GUP-2 integration test."
