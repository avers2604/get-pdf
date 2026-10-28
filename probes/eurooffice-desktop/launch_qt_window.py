#!/usr/bin/env python3
"""GD-31J.2: open the real compiled Qt DIAGNOSTIC window.

Only works with a binary explicitly supplied by the operator. Does not open
documents, access networks, print or initialize EuroOffice SDK/CEF.
Close the diagnostic window to exit. Never use as a production launcher.
"""
import json
import pathlib
import subprocess
import sys

PINNED = "6a671bb1ad238c61965a64de3a005c420940c814"
PROTOCOL = "getdoc-eurodesktop-probe/1"


def main(argv):
    if len(argv) != 2:
        print("Usage: python launch_qt_window.py /approved/getdoc-eurooffice-qt-probe",
              file=sys.stderr)
        return 2
    executable = pathlib.Path(argv[1]).resolve(strict=True)
    if not executable.is_file():
        return 2
    request = {"command": "hello", "protocol": PROTOCOL, "upstreamCommit": PINNED}
    # This standalone script is for a supervised manual lab only. The GET
    # desktop integration must additionally enforce the C# SHA256 policy.
    process = subprocess.Popen(
        [str(executable), "--gui-probe"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=None)
    try:
        assert process.stdin is not None and process.stdout is not None
        process.stdin.write((json.dumps(request, separators=(",", ":")) + "\n").encode())
        process.stdin.flush()
        reply = process.stdout.readline(16 * 1024)
        info = json.loads(reply)
        if (info.get("features") != [] or info.get("protocol") != PROTOCOL or
            info.get("upstreamCommit") != PINNED or
            info.get("engine") != "EuroOfficeDesktopEditors" or len(info) != 4):
            raise ValueError("Untrusted or unsupported Qt probe response")
        print("Qt diagnostic window running; close its window to exit.")
        return process.wait()
    except (BrokenPipeError, OSError, ValueError, json.JSONDecodeError) as exc:
        print("Qt probe rejected:", type(exc).__name__, file=sys.stderr)
        return 2
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
