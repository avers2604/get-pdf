# GET Document <-> LibreOffice editor bridge (runs on LibreOffice's own Python).
# LibreOffice opens each document in its own hidden window without menus, toolbars, sidebar or
# info bars; GET Document reparents that window into its tab and drives it from GET DS controls.
# Protocol: one JSON object per line.
#   stdin : {"re": n, "op": "open"|"show"|"focus"|"dispatch"|"save"|"saveas"|"pdf"|"close", ...}
#   stdout: {"re": n, "ok": true, ...} | {"re": n, "ok": false, "error": "..."}
#           {"event": "state"|"modified"|"command"|"closed", "id": ..., ...}   (any time)
# Documents never run macros. When stdin closes the bridge closes every document and LibreOffice.
import json
import os
import sys
import threading
import time

import uno
import unohelper
from com.sun.star.beans import PropertyValue
from com.sun.star.connection import NoConnectException
from com.sun.star.frame import (XDispatch, XDispatchProviderInterceptor, XFrameActionListener,
                                XLayoutManagerListener, XStatusListener)
from com.sun.star.task import XInteractionHandler
from com.sun.star.util import XCloseListener, XModifyListener

# com.sun.star.document.MacroExecMode.FROM_LIST_NO_WARN: only macros from trusted locations run, and
# the profile trusts none - so nothing runs, without the "macros disabled" bar NEVER_EXECUTE shows.
NO_MACROS_NO_BAR = 7
NO_LINK_UPDATE = 0         # com.sun.star.document.UpdateDocMode.NO_UPDATE

# Toolbar state GET Document mirrors (toggle buttons, font boxes, undo/redo availability).
COMMON_STATE = [".uno:Undo", ".uno:Redo", ".uno:Bold", ".uno:Italic", ".uno:Underline",
                ".uno:CharFontName", ".uno:FontHeight"]
STATE = {
    "writer": COMMON_STATE + [".uno:LeftPara", ".uno:CenterPara", ".uno:RightPara", ".uno:JustifyPara",
                              ".uno:DefaultBullet", ".uno:DefaultNumbering", ".uno:StyleApply"],
    "calc": COMMON_STATE + [".uno:AlignLeft", ".uno:AlignHorizontalCenter", ".uno:AlignRight",
                            ".uno:ToggleMergeCells", ".uno:WrapText"],
    "impress": COMMON_STATE + [".uno:LeftPara", ".uno:CenterPara", ".uno:RightPara",
                               ".uno:DefaultBullet", ".uno:DefaultNumbering"],
}
# Features are intentionally closed over this whitelist: a GET toolbar action cannot execute
# arbitrary UNO URLs, access files, or open hidden LibreOffice dialogs.
FEATURES = {
    "writer-toc-insert": ("writer", True),
    "writer-toc-update": ("writer", True),
    "calc-freeze-top-row": ("calc", False),
    "calc-freeze-first-column": ("calc", False),
    "calc-unfreeze": ("calc", False),
    "calc-recalculate": ("calc", True),
    "calc-print-used-area": ("calc", True),
    "calc-clear-print-area": ("calc", True),
    "calc-repeat-first-row": ("calc", True),
    "calc-stop-repeat-rows": ("calc", True),
    "impress-duplicate-slide": ("impress", True),
}

# Keyboard shortcuts that would open LibreOffice's own dialogs are handed to GET Document instead.
INTERCEPTED = {".uno:Save", ".uno:SaveAs", ".uno:SaveAll", ".uno:Print", ".uno:PrintDefault",
               ".uno:PrintPreview", ".uno:ExportToPDF", ".uno:ExportDirectToPDF", ".uno:CloseDoc",
               ".uno:CloseWin", ".uno:Quit", ".uno:Open", ".uno:AddDirect", ".uno:NewDoc"}

_out = threading.Lock()


def emit(message):
    with _out:
        sys.stdout.write(json.dumps(message, ensure_ascii=False) + "\n")
        sys.stdout.flush()


def prop(name, value):
    p = PropertyValue()
    p.Name = name
    p.Value = value
    return p


def plain(value):
    """UNO status value -> JSON (bool, number, font name, style name...)."""
    if value is None or isinstance(value, (bool, int, float, str)):
        return value
    type_name = getattr(value, "typeName", "") or ""
    if type_name.endswith("FontDescriptor") or (hasattr(value, "Name") and hasattr(value, "Family")):
        return value.Name            # .uno:CharFontName
    if type_name.endswith("FontHeight") or (hasattr(value, "Height") and hasattr(value, "Prop")):
        return value.Height          # .uno:FontHeight
    if type_name.endswith("Template") or (hasattr(value, "StyleName") and hasattr(value, "Value")):
        return value.StyleName       # .uno:StyleApply
    for attribute in ("Name", "Height", "Value"):
        if hasattr(value, attribute):
            return plain(getattr(value, attribute))
    return str(value)


def kind_of(doc):
    if doc.supportsService("com.sun.star.sheet.SpreadsheetDocument"):
        return "calc"
    if doc.supportsService("com.sun.star.presentation.PresentationDocument"):
        return "impress"
    if doc.supportsService("com.sun.star.drawing.DrawingDocument"):
        return "draw"
    return "writer"


PDF_FILTER = {"writer": "writer_pdf_Export", "calc": "calc_pdf_Export",
              "impress": "impress_pdf_Export", "draw": "draw_pdf_Export"}


INFOBARS = ("macro", "readonly", "hiddenparagraphs", "hyphenationmissing", "whatsnew", "getinvolved",
            "donate", "signature", "hidetrackchanges", "safemode", "enablecontent")
LAYOUT_VISIBLE, LAYOUT_ELEMENT_VISIBLE = 3, 6     # com.sun.star.frame.LayoutManagerEvents
_tidying = threading.local()


def hide_chrome(frame):
    """No LibreOffice menu bar, toolbars, status bar, sidebar or info bars: GET Document draws its own
    controls. LibreOffice restores some of them whenever the window is activated, so this runs again
    from the frame and layout listeners below."""
    if getattr(_tidying, "busy", False):
        return
    _tidying.busy = True
    try:
        layout = frame.LayoutManager
        controller = frame.getController()
        model = controller.getModel() if controller is not None else None
        spreadsheet = model is not None and model.supportsService("com.sun.star.sheet.SpreadsheetDocument")
        if not spreadsheet:   # in Calc it would also hide the formula bar, which stays
            try:
                layout.setPropertyValue("HideCurrentUI", True)
            except Exception:
                pass
        for element in layout.getElements():
            layout.hideElement(element.ResourceURL)
        layout.setVisible(False)   # also keeps contextual toolbars (tables, images) from popping up
        if controller is not None:
            for bar in INFOBARS:
                try:
                    if controller.hasInfobar(bar):
                        controller.removeInfobar(bar)
                except Exception:
                    pass
            try:
                controller.getSidebar().setVisible(False)   # idempotent; the tab bar returns on activation
            except Exception:
                pass

    except Exception:
        pass
    finally:
        _tidying.busy = False


class ChromeGuard(unohelper.Base, XFrameActionListener, XLayoutManagerListener):
    def __init__(self, frame):
        self.frame = frame

    def frameAction(self, event):
        if event.Action.value in ("FRAME_ACTIVATED", "FRAME_UI_ACTIVATED", "COMPONENT_ATTACHED",
                                  "COMPONENT_REATTACHED", "CONTEXT_CHANGED"):
            hide_chrome(self.frame)

    def layoutEvent(self, source, event, info):
        if event in (LAYOUT_VISIBLE, LAYOUT_ELEMENT_VISIBLE):
            hide_chrome(self.frame)

    def disposing(self, source):
        pass


class SidebarGuard(unohelper.Base, XStatusListener):
    """LibreOffice brings the sidebar back on its own (Calc, after activation): hide it whenever it shows."""
    def __init__(self, frame):
        self.frame = frame

    def statusChanged(self, event):
        if event.State is True:
            try:
                self.frame.getController().getSidebar().setVisible(False)
            except Exception:
                pass

    def disposing(self, source):
        pass


class StateListener(unohelper.Base, XStatusListener):
    def __init__(self, doc_id):
        self.doc_id = doc_id

    def statusChanged(self, event):
        emit({"event": "state", "id": self.doc_id, "cmd": event.FeatureURL.Complete,
              "enabled": bool(event.IsEnabled), "value": plain(event.State)})

    def disposing(self, source):
        pass


class ModifyListener(unohelper.Base, XModifyListener):
    def __init__(self, doc_id, doc):
        self.doc_id = doc_id
        self.doc = doc

    def modified(self, event):
        try:
            emit({"event": "modified", "id": self.doc_id, "value": bool(self.doc.isModified())})
        except Exception:
            pass

    def disposing(self, source):
        pass


class CloseListener(unohelper.Base, XCloseListener):
    def __init__(self, bridge, doc_id):
        self.bridge = bridge
        self.doc_id = doc_id

    def queryClosing(self, source, owner):
        pass

    def notifyClosing(self, source):
        if self.bridge.docs.pop(self.doc_id, None) is not None:
            emit({"event": "closed", "id": self.doc_id})

    def disposing(self, source):
        pass


class Interceptor(unohelper.Base, XDispatchProviderInterceptor, XDispatch):
    def __init__(self, doc_id):
        self.doc_id = doc_id
        self.slave = None
        self.master = None

    def getSlaveDispatchProvider(self):
        return self.slave

    def setSlaveDispatchProvider(self, provider):
        self.slave = provider

    def getMasterDispatchProvider(self):
        return self.master

    def setMasterDispatchProvider(self, provider):
        self.master = provider

    def queryDispatch(self, url, target, flags):
        if url.Complete.split("?")[0] in INTERCEPTED:
            return self
        return self.slave.queryDispatch(url, target, flags) if self.slave else None

    def queryDispatches(self, requests):
        return tuple(self.queryDispatch(r.FeatureURL, r.FrameName, r.SearchFlags) for r in requests)

    def dispatch(self, url, arguments):
        emit({"event": "command", "id": self.doc_id, "cmd": url.Complete.split("?")[0]})

    def addStatusListener(self, listener, url):
        pass

    def removeStatusListener(self, listener, url):
        pass


class Interactions(unohelper.Base, XInteractionHandler):
    """Answers LibreOffice's questions during a hidden load, where no dialog could be shown.
    A lock left by a crash of this user's own LibreOffice is ignored (the document opens for editing);
    a lock held by someone else opens the document read-only; everything else (passwords, repairs)
    is cancelled so the load fails cleanly instead of waiting for an invisible dialog."""
    LOCKS = ("OwnLockOnDocumentRequest", "LockedDocumentRequest", "LockFileIgnoreRequest", "LockFileCorruptRequest")

    def handle(self, request):
        question = request.getRequest()
        name = getattr(question, "typeName", "") or type(question).__name__
        wanted = "XInteractionApprove" if any(lock in name for lock in self.LOCKS) else "XInteractionAbort"
        fallback = None
        for continuation in request.getContinuations():
            types = [t.typeName for t in continuation.getTypes()] if hasattr(continuation, "getTypes") else []
            if any(t.endswith(wanted) for t in types):
                continuation.select()
                return
            if any(t.endswith("XInteractionAbort") for t in types):
                fallback = continuation
        if fallback is not None:
            fallback.select()


class Document:
    def __init__(self, doc, frame, kind):
        self.doc = doc
        self.frame = frame
        self.kind = kind
        self.listeners = []


class Bridge:
    def __init__(self, pipe, seconds):
        self.pipe = pipe
        self.seconds = seconds
        self.docs = {}
        self.connect()

    def connect(self):
        local = uno.getComponentContext()
        resolver = local.ServiceManager.createInstanceWithContext("com.sun.star.bridge.UnoUrlResolver", local)
        deadline = time.time() + self.seconds
        while True:
            try:
                self.ctx = resolver.resolve("uno:pipe,name=%s;urp;StarOffice.ComponentContext" % self.pipe)
                break
            except NoConnectException:
                if time.time() > deadline:
                    raise
                time.sleep(0.5)
        smgr = self.ctx.ServiceManager
        self.desktop = smgr.createInstanceWithContext("com.sun.star.frame.Desktop", self.ctx)
        self.dispatcher = smgr.createInstanceWithContext("com.sun.star.frame.DispatchHelper", self.ctx)
        self.urls = smgr.createInstanceWithContext("com.sun.star.util.URLTransformer", self.ctx)

    def own_profile(self):
        """URL of this LibreOffice's profile as lock files record it (without the trailing /user)."""
        if not hasattr(self, "_own_profile"):
            try:
                substitution = self.ctx.ServiceManager.createInstanceWithContext("com.sun.star.util.PathSubstitution", self.ctx)
                user = substitution.substituteVariables("$(userurl)", True)
                self._own_profile = user[:-len("/user")] if user.endswith("/user") else user
            except Exception:
                self._own_profile = ""
        return self._own_profile

    def drop_stale_lock(self, path):
        """A lock written by this editor's own profile can only be left over from a crash: the editor
        is not holding the document now (GET Document never opens a file twice). Locks of any other
        LibreOffice (a colleague, the user's own LibreOffice) are respected - the file opens read-only."""
        folder, name = os.path.split(path)
        lock = os.path.join(folder, ".~lock." + name + "#")
        try:
            with open(lock, encoding="utf-8", errors="replace") as handle:
                fields = handle.read().strip().rstrip(";").split(",")
            own = self.own_profile()
            if own and len(fields) >= 5 and fields[4].rstrip("/").lower() == own.rstrip("/").lower():
                os.remove(lock)
        except OSError:
            pass

    def url(self, command):
        target = uno.createUnoStruct("com.sun.star.util.URL")
        target.Complete = command
        return self.urls.parseStrict(target)[1]

    def document(self, request):
        entry = self.docs.get(request["id"])
        if entry is None:
            raise RuntimeError("документ уже закрыт")
        return entry

    # --- operations -------------------------------------------------------------------------
    def op_open(self, request):
        doc_id = request["id"]
        self.drop_stale_lock(request["path"])
        arguments = [prop("Hidden", True), prop("MacroExecutionMode", NO_MACROS_NO_BAR),
                     prop("UpdateDocMode", NO_LINK_UPDATE), prop("InteractionHandler", Interactions())]
        if request["path"].lower().endswith(".csv"):
            # comma separated, double quotes, UTF-8, first line as is - no text import dialog
            arguments += [prop("FilterName", "Text - txt - csv (StarCalc)"), prop("FilterOptions", "44,34,76,1")]
        doc = self.desktop.loadComponentFromURL(uno.systemPathToFileUrl(request["path"]), "_blank", 0, tuple(arguments))
        if doc is None:
            raise RuntimeError("LibreOffice не смог открыть документ")
        controller = doc.getCurrentController()
        frame = controller.getFrame()
        kind = kind_of(doc)
        entry = Document(doc, frame, kind)
        self.docs[doc_id] = entry
        hide_chrome(frame)
        guard = ChromeGuard(frame)
        frame.addFrameActionListener(guard)
        try:
            frame.LayoutManager.addLayoutManagerEventListener(guard)
        except Exception:
            pass
        if kind == "calc":
            try:   # the formula bar stays: it is how cell contents and formulas are edited
                self.dispatcher.executeDispatch(frame, ".uno:InputLineVisible", "", 0, (prop("InputLineVisible", True),))
            except Exception:
                pass
        frame.registerDispatchProviderInterceptor(Interceptor(doc_id))
        modify = ModifyListener(doc_id, doc)
        doc.addModifyListener(modify)
        doc.addCloseListener(CloseListener(self, doc_id))
        state = StateListener(doc_id)
        for command in STATE.get(kind, COMMON_STATE):
            url = self.url(command)
            dispatch = frame.queryDispatch(url, "", 0)
            if dispatch is not None:
                dispatch.addStatusListener(state, url)
                entry.listeners.append((dispatch, url))
        entry.state = state
        sidebar_url = self.url(".uno:Sidebar")
        sidebar = frame.queryDispatch(sidebar_url, "", 0)
        if sidebar is not None:
            entry.sidebar_guard = SidebarGuard(frame)
            sidebar.addStatusListener(entry.sidebar_guard, sidebar_url)
        frame.Title = request["marker"]
        readonly = False
        try:
            readonly = bool(doc.isReadonly())
        except Exception:
            pass
        return {"kind": kind, "readonly": readonly}

    def op_show(self, request):
        entry = self.document(request)
        entry.frame.getContainerWindow().setVisible(True)
        hide_chrome(entry.frame)     # showing the window brings the menu bar back: remove it for good
        entry.frame.getComponentWindow().setFocus()
        return {}

    def op_focus(self, request):
        self.document(request).frame.getComponentWindow().setFocus()
        return {}

    def op_dispatch(self, request):
        entry = self.document(request)
        arguments = tuple(prop(name, float(value) if name.endswith("Height") and isinstance(value, int) else value)
                          for name, value in (request.get("args") or {}).items())
        self.dispatcher.executeDispatch(entry.frame, request["cmd"], "", 0, arguments)
        if request.get("focus", True):
            entry.frame.getComponentWindow().setFocus()
        return {}

    def op_feature(self, request):
        entry = self.document(request)
        feature = request.get("feature")
        permission = FEATURES.get(feature)
        if permission is None or permission[0] != entry.kind:
            raise RuntimeError("команда недоступна для этого редактора")
        if permission[1] and entry.doc.isReadonly():
            raise RuntimeError("документ открыт только для чтения")

        doc = entry.doc
        if feature == "writer-toc-insert":
            cursor = doc.getCurrentController().getViewCursor()
            toc = doc.createInstance("com.sun.star.text.ContentIndex")
            if toc is None:
                raise RuntimeError("движок не поддерживает оглавление")
            toc.CreateFromOutline = True
            toc.Level = 10
            cursor.getText().insertTextContent(cursor, toc, False)
            toc.update()
            return {"count": doc.getDocumentIndexes().getCount()}

        if feature == "writer-toc-update":
            indexes = doc.getDocumentIndexes()
            for position in range(indexes.getCount()):
                indexes.getByIndex(position).update()
            return {"count": indexes.getCount()}

        if feature in ("calc-freeze-top-row", "calc-freeze-first-column", "calc-unfreeze"):
            positions = {
                "calc-freeze-top-row": (0, 1),
                "calc-freeze-first-column": (1, 0),
                "calc-unfreeze": (0, 0),
            }
            controller = doc.getCurrentController()
            if not hasattr(controller, "freezeAtPosition"):
                raise RuntimeError("движок не поддерживает закрепление строк и столбцов")
            controller.freezeAtPosition(*positions[feature])
            return {"frozen": bool(controller.hasFrozenPanes())}

        if feature == "calc-recalculate":
            doc.calculateAll()
            return {}

        if feature in ("calc-print-used-area", "calc-clear-print-area",
                       "calc-repeat-first-row", "calc-stop-repeat-rows"):
            controller = doc.getCurrentController()
            sheet = controller.getActiveSheet()
            if sheet is None or not hasattr(sheet, "setPrintAreas"):
                raise RuntimeError("движок не поддерживает настройки печати таблицы")
            if feature == "calc-print-used-area":
                cursor = sheet.createCursor()
                cursor.gotoEndOfUsedArea(True)
                area = cursor.getRangeAddress()
                # Avoid accidentally producing a PDF containing millions of mostly empty cells.
                cells = (area.EndColumn - area.StartColumn + 1) * (area.EndRow - area.StartRow + 1)
                if cells <= 0 or cells > 2000000:
                    raise RuntimeError("слишком большая область печати; уточните диапазон в таблице")
                sheet.setPrintAreas((area,))
                return {"cells": cells}
            if feature == "calc-clear-print-area":
                sheet.setPrintAreas(())
                return {}
            if feature == "calc-repeat-first-row":
                row = sheet.getCellRangeByPosition(0, 0, 0, 0).getRangeAddress()
                sheet.setTitleRows(row)
                sheet.setPrintTitleRows(True)
                return {}
            if feature == "calc-stop-repeat-rows":
                sheet.setPrintTitleRows(False)
                return {}

        if feature == "impress-duplicate-slide":
            controller = doc.getCurrentController()
            source = controller.getCurrentPage()
            if source is None or not hasattr(doc, "duplicate"):
                raise RuntimeError("движок не поддерживает дублирование слайда")
            duplicate = doc.duplicate(source)
            controller.setCurrentPage(duplicate)
            return {"count": doc.getDrawPages().getCount()}

        raise RuntimeError("неизвестная команда")

    def op_save(self, request):
        entry = self.document(request)
        entry.doc.store()
        return {"modified": bool(entry.doc.isModified())}

    def op_saveas(self, request):
        # A copy: the tab keeps editing the original file.
        entry = self.document(request)
        entry.doc.storeToURL(uno.systemPathToFileUrl(request["path"]), (prop("FilterName", request["filter"]),))
        return {"modified": bool(entry.doc.isModified())}

    def op_pdf(self, request):
        entry = self.document(request)
        entry.doc.storeToURL(uno.systemPathToFileUrl(request["path"]),
                             (prop("FilterName", PDF_FILTER.get(entry.kind, "writer_pdf_Export")),))
        return {}

    def op_close(self, request):
        entry = self.docs.pop(request["id"], None)
        if entry is not None:
            for dispatch, url in entry.listeners:
                try:
                    dispatch.removeStatusListener(entry.state, url)
                except Exception:
                    pass
            try:
                entry.doc.close(True)
            except Exception:
                entry.frame.dispose()
        return {}

    def handle(self, request):
        operation = getattr(self, "op_" + request.get("op", ""), None)
        if operation is None:
            raise RuntimeError("неизвестная операция")
        return operation(request)

    def shutdown(self):
        for doc_id in list(self.docs):
            try:
                self.op_close({"id": doc_id})
            except Exception:
                pass
        try:
            self.desktop.terminate()
        except Exception:
            pass


def main():
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    bridge = Bridge(sys.argv[1], float(sys.argv[2]) if len(sys.argv) > 2 else 120.0)
    emit({"event": "ready"})
    try:
        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue
            request = {}
            try:
                request = json.loads(line)
                try:
                    result = bridge.handle(request)
                except Exception as error:
                    # soffice restarts itself once after initialising a profile (exit 81):
                    # reconnect to the new instance; documents of the old one are gone.
                    if "disposed" not in str(error).lower() or request.get("op") != "open":
                        raise
                    bridge.connect()
                    bridge.docs.clear()
                    result = bridge.handle(request)
                reply = {"re": request.get("re"), "ok": True}
                reply.update(result)
                emit(reply)
            except Exception as error:  # report and keep serving; the caller decides
                emit({"re": request.get("re"), "ok": False, "error": str(error)[:400]})
    finally:
        bridge.shutdown()


if __name__ == "__main__":
    main()
