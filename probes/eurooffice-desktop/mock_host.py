#!/usr/bin/env python3
"""GD-31J TEST DOUBLE ONLY. NOT EuroOffice, NOT an editor.

Accepts one JSONL hello and responds with ZERO supported operations. This is
intentionally impossible to use to open, edit, print, or export a document.
Never deploy in the GET desktop application.
"""
import json
import sys

PROTOCOL = "getdoc-eurodesktop-probe/1"
PINNED = "6a671bb1ad238c61965a64de3a005c420940c814"


def response(request):
    if not isinstance(request, dict) or set(request) != {"command", "protocol", "upstreamCommit"}:
        raise ValueError("only a strict hello request is accepted")
    if request["command"] != "hello" or request["protocol"] != PROTOCOL or request["upstreamCommit"] != PINNED:
        raise ValueError("unknown or unauthorized operation")
    return {
        "protocol": PROTOCOL,
        "engine": "EuroOfficeDesktopEditors",
        "upstreamCommit": PINNED,
        "features": [],
    }


def main():
    raw = sys.stdin.buffer.readline(16 * 1024 + 1)
    if not raw or len(raw) > 16 * 1024 or not raw.endswith(b"\n"):
        return 2
    try:
        request = json.loads(raw.decode("utf-8"))
        result = response(request)
    except (UnicodeDecodeError, json.JSONDecodeError, ValueError):
        return 2
    sys.stdout.write(json.dumps(result, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()
    return 0


if __name__ == "__main__":
    sys.exit(main())
