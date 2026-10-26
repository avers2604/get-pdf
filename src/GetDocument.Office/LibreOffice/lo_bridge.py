# GET Document <-> LibreOffice UNO bridge (runs on LibreOffice's own Python).
# Keeps one warm headless LibreOffice busy with PDF exports so a preview takes seconds
# instead of a full LibreOffice start. Protocol: one JSON object per line.
#   stdin : {"in": "<document path>", "out": "<pdf path>"}
#   stdout: {"ok": true} | {"ok": false, "error": "..."}
# Documents are loaded hidden, read-only, without running macros or updating links.
# When stdin closes (GET Document exited or crashed) the bridge shuts LibreOffice down.
import json
import sys
import time

import uno
from com.sun.star.beans import PropertyValue
from com.sun.star.connection import NoConnectException

NEVER_EXECUTE_MACROS = 0   # com.sun.star.document.MacroExecMode.NEVER_EXECUTE
NO_LINK_UPDATE = 0         # com.sun.star.document.UpdateDocMode.NO_UPDATE


def prop(name, value):
    p = PropertyValue()
    p.Name = name
    p.Value = value
    return p


def connect(pipe, seconds):
    local = uno.getComponentContext()
    resolver = local.ServiceManager.createInstanceWithContext("com.sun.star.bridge.UnoUrlResolver", local)
    deadline = time.time() + seconds
    while True:
        try:
            ctx = resolver.resolve("uno:pipe,name=%s;urp;StarOffice.ComponentContext" % pipe)
            return ctx.ServiceManager.createInstanceWithContext("com.sun.star.frame.Desktop", ctx)
        except NoConnectException:
            if time.time() > deadline:
                raise
            time.sleep(0.5)


def export_filter(doc):
    if doc.supportsService("com.sun.star.sheet.SpreadsheetDocument"):
        return "calc_pdf_Export"
    if doc.supportsService("com.sun.star.presentation.PresentationDocument"):
        return "impress_pdf_Export"
    if doc.supportsService("com.sun.star.drawing.DrawingDocument"):
        return "draw_pdf_Export"
    return "writer_pdf_Export"


def convert(desktop, source, target):
    doc = desktop.loadComponentFromURL(
        uno.systemPathToFileUrl(source), "_blank", 0,
        (prop("Hidden", True), prop("ReadOnly", True),
         prop("MacroExecutionMode", NEVER_EXECUTE_MACROS), prop("UpdateDocMode", NO_LINK_UPDATE)))
    if doc is None:
        raise RuntimeError("LibreOffice не смог открыть документ")
    try:
        doc.storeToURL(uno.systemPathToFileUrl(target), (prop("FilterName", export_filter(doc)),))
    finally:
        doc.close(True)


def main():
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    pipe = sys.argv[1]
    desktop = connect(pipe, float(sys.argv[2]) if len(sys.argv) > 2 else 120.0)
    print(json.dumps({"ok": True, "ready": True}), flush=True)
    try:
        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue
            try:
                request = json.loads(line)
                try:
                    convert(desktop, request["in"], request["out"])
                except Exception as error:
                    # soffice restarts itself once after initialising a profile (exit 81) and
                    # can drop the connection mid-call: reconnect to the new instance, retry once.
                    if "disposed" not in str(error).lower():
                        raise
                    desktop = connect(pipe, 120.0)
                    convert(desktop, request["in"], request["out"])
                print(json.dumps({"ok": True}), flush=True)
            except Exception as error:  # report and keep serving; the caller decides
                print(json.dumps({"ok": False, "error": str(error)[:400]}), flush=True)
    finally:
        try:
            desktop.terminate()
        except Exception:
            pass


if __name__ == "__main__":
    main()
