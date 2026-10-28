#include "osr_input.h"
#ifdef GETDOC_GD31J14_POINTER_INPUT
#if !defined(__linux__)
#error GD-31J.14 pointer IPC is supported only on the isolated Linux Wayland lab
#endif
#include "cefview.h"
#include "qcefview.h"
#include <QJsonDocument>
#include <QJsonObject>
#include <QJsonParseError>
#include <QWidget>
#include <algorithm>
#include <cerrno>
#include <cmath>
#include <fcntl.h>
#include <initializer_list>
#include <utility>
#include <unistd.h>

namespace {
constexpr int kMaximumInputLine = 4096;
constexpr int kMaximumInputMessages = 128;
constexpr int kRatePerSecond = 30;
constexpr int kCefLeftButton = 1 << 4; // Verified upstream GetCefModifiers Qt::LeftButton
constexpr int kCefShift = 1 << 1;
// Explicit Windows virtual-key whitelist. No Ctrl/Alt/Meta shortcuts.
int ApprovedNavigationVk(const QString& key) {
    if (key == "ArrowLeft") return 37;
    if (key == "ArrowUp") return 38;
    if (key == "ArrowRight") return 39;
    if (key == "ArrowDown") return 40;
    if (key == "Home") return 36;
    if (key == "End") return 35;
    if (key == "PageUp") return 33;
    if (key == "PageDown") return 34;
    if (key == "Escape") return 27;
    if (key == "Backspace") return 8;
    if (key == "Delete") return 46;
    if (key == "Enter") return 13;
    if (key == "Tab") return 9;
    return 0;
}
bool AllowedCommittedCharacter(int code) {
    return code >= 0x20 && code <= 0xffff &&
           !(code >= 0x7f && code <= 0x9f) &&
           !(code >= 0xd800 && code <= 0xdfff) &&
           !(code >= 0x200b && code <= 0x200f) &&
           !(code >= 0x202a && code <= 0x202e) &&
           !(code >= 0x2060 && code <= 0x2069) &&
           code != 0xfeff;
}

bool ExactKeys(const QJsonObject& object, std::initializer_list<const char*> fields) {
    if (object.size() != static_cast<int>(fields.size())) return false;
    for (const char* name : fields)
        if (!object.contains(name)) return false;
    return true;
}
bool BoundedInteger(const QJsonValue& value, int lowest, int highest) {
    if (!value.isDouble()) return false;
    const double x = value.toDouble();
    return std::isfinite(x) && x >= lowest && x <= highest && std::floor(x) == x;
}
bool CalculatePosition(QCefView* view, const QJsonObject& request, int& x, int& y) {
    if (!view || !BoundedInteger(request.value("x10000"), 0, 9999) ||
        !BoundedInteger(request.value("y10000"), 0, 9999))
        return false;
    const double dpr = view->devicePixelRatioF();
    if (!std::isfinite(dpr) || dpr < 0.5 || dpr > 4.0 ||
        view->width() < 1 || view->width() > 8192 ||
        view->height() < 1 || view->height() > 8192)
        return false;
    const int physicalWidth = static_cast<int>(std::floor(view->width() * dpr));
    const int physicalHeight = static_cast<int>(std::floor(view->height() * dpr));
    if (physicalWidth < 1 || physicalHeight < 1 ||
        physicalWidth > 16384 || physicalHeight > 16384)
        return false;
    x = std::clamp(static_cast<int>(std::floor(
        request.value("x10000").toInt() / 10000.0 * physicalWidth)), 0, physicalWidth - 1);
    y = std::clamp(static_cast<int>(std::floor(
        request.value("y10000").toInt() / 10000.0 * physicalHeight)), 0, physicalHeight - 1);
    return true;
}
} // namespace

OsrInputPump::OsrInputPump(QString sessionId, QString tabId, bool clicksApproved,
                           bool keyboardApproved)
    : m_sessionId(std::move(sessionId)), m_tabId(std::move(tabId)),
      m_clicksApproved(clicksApproved), m_keyboardApproved(keyboardApproved) {}

bool OsrInputPump::Enable() {
    const int flags = ::fcntl(STDIN_FILENO, F_GETFL);
    if (flags < 0 || ::fcntl(STDIN_FILENO, F_SETFL, flags | O_NONBLOCK) < 0)
        return false;
    m_rateWindow.start();
    return true;
}

bool OsrInputPump::Poll(QCefView* view, CCefViewEditor* editor) {
    if (!view || !editor) return false;
    char buffer[1024];
    // One read per tick keeps Qt/CEF's owner thread responsive.
    const ssize_t count = ::read(STDIN_FILENO, buffer, sizeof(buffer));
    if (count == 0) return false; // pipe closed => terminate lab
    if (count < 0) return errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR;
    m_pending.append(buffer, static_cast<qsizetype>(count));
    if (m_pending.size() >= kMaximumInputLine) return false;
    int lines = 0;
    while (true) {
        const qsizetype newline = m_pending.indexOf('\n');
        if (newline < 0) break;
        if (++lines > 16) return false; // bounded event-loop work per tick
        const QByteArray line = m_pending.left(newline);
        m_pending.remove(0, newline + 1);
        if (line.isEmpty() || !Dispatch(line, view, editor)) return false;
    }
    return true;
}

bool OsrInputPump::Dispatch(const QByteArray& bytes,
                             QCefView* view, CCefViewEditor* editor) {
    if (m_lastSequence >= kMaximumInputMessages) return false;
    QJsonParseError error;
    const QJsonDocument parsed = QJsonDocument::fromJson(bytes, &error);
    if (error.error != QJsonParseError::NoError || !parsed.isObject()) return false;
    const QJsonObject request = parsed.object();
    // Reject duplicate fields, whitespace and ambiguous number encodings.
    if (QJsonDocument(request).toJson(QJsonDocument::Compact) != bytes) return false;
    if (request.value("protocol").toString() != "getdoc-cef-osr-input/1" ||
        request.value("sessionId").toString() != m_sessionId ||
        request.value("tabId").toString() != m_tabId ||
        !BoundedInteger(request.value("sequence"), m_lastSequence + 1, m_lastSequence + 1))
        return false;

    const QString command = request.value("command").toString();
    if (m_rateWindow.elapsed() >= 1000) {
        m_rateCount = 0;
        m_rateWindow.restart();
    }
    if (++m_rateCount > kRatePerSecond) return false;
    if (command == "focus" &&
        ExactKeys(request, {"command","focused","protocol","sequence","sessionId","tabId"})) {
        if (!request.value("focused").isBool()) return false;
        const bool enable = request.value("focused").toBool();
        if (!enable && m_leftDown) return false;
        editor->focus(enable); // Verified pinned EuroOffice CCefView::focus(bool)
        m_focused = enable;
    } else if (command == "move" &&
               ExactKeys(request, {"command","protocol","sequence","sessionId",
                                   "tabId","x10000","y10000"})) {
        int x = 0, y = 0;
        if (!m_focused || !CalculatePosition(view, request, x, y)) return false;
        editor->SendMouseMoveEvent(x, y, false, m_leftDown ? kCefLeftButton : 0);
    } else if (command == "wheel" &&
               ExactKeys(request, {"command","deltaX","deltaY","protocol","sequence",
                                   "sessionId","tabId","x10000","y10000"})) {
        int x = 0, y = 0;
        if (!m_focused || m_leftDown ||
            !CalculatePosition(view, request, x, y) ||
            !BoundedInteger(request.value("deltaX"), -120, 120) ||
            !BoundedInteger(request.value("deltaY"), -120, 120) ||
            (request.value("deltaX").toInt() == 0 && request.value("deltaY").toInt() == 0))
            return false;
        editor->SendMouseWheelEvent(x, y, request.value("deltaX").toInt(),
                                   request.value("deltaY").toInt(), 0);
    } else if (command == "left-down" && m_clicksApproved &&
               ExactKeys(request, {"clickCount", "command", "protocol", "sequence",
                                   "sessionId", "tabId", "x10000", "y10000"})) {
        int x = 0, y = 0;
        if (!m_focused || m_leftDown ||
            !CalculatePosition(view, request, x, y) ||
            !BoundedInteger(request.value("clickCount"), 1, 2))
            return false;
        const int clickCount = request.value("clickCount").toInt();
        editor->SendMouseClickEvent(x, y, 1, false, kCefLeftButton, clickCount);
        m_leftDown = true;
        m_clickCount = clickCount;
    } else if (command == "left-up" && m_clicksApproved &&
               ExactKeys(request, {"command", "protocol", "sequence",
                                   "sessionId", "tabId", "x10000", "y10000"})) {
        int x = 0, y = 0;
        if (!m_focused || !m_leftDown || !CalculatePosition(view, request, x, y))
            return false;
        editor->SendMouseClickEvent(x, y, 1, true, 0, m_clickCount);
        m_leftDown = false;
        m_clickCount = 1;
    } else if (command == "nav-key" && m_keyboardApproved &&
               ExactKeys(request, {"command", "key", "protocol", "sequence",
                                   "sessionId", "shift", "tabId"})) {
        const int vk = ApprovedNavigationVk(request.value("key").toString());
        if (!m_focused || m_leftDown || vk == 0 || !request.value("shift").isBool())
            return false;
        const int modifiers = request.value("shift").toBool() ? kCefShift : 0;
        // This is ONE atomic key down/up pair on the Qt owner thread.
        editor->SendKeyEvent(0, vk, modifiers, L"");
        editor->SendKeyEvent(2, vk, modifiers, L"");
    } else if (command == "text-char" && m_keyboardApproved &&
               ExactKeys(request, {"codePoint", "command", "protocol",
                                   "sequence", "sessionId", "tabId"})) {
        if (!m_focused || m_leftDown ||
            !BoundedInteger(request.value("codePoint"), 0x20, 0xffff))
            return false;
        const int code = request.value("codePoint").toInt();
        if (!AllowedCommittedCharacter(code)) return false;
        const wchar_t character = static_cast<wchar_t>(code);
        // Upstream QCefView::inputMethodEvent uses 0/3/2 with zero VK.
        // Single committed BMP codepoint ONLY; no IME preedit/candidate list.
        std::wstring units(2, character);
        units.push_back(0);
        editor->SendKeyEvent(0, 0, 0, units);
        editor->SendKeyEvent(3, 0, 0, units);
        editor->SendKeyEvent(2, 0, 0, units);
    } else {
        // No generic key codes, Ctrl/Alt/Meta shortcuts, IME composition,
        // clipboard, arbitrary strings, filenames, JS or printing commands.
        return false;
    }
    ++m_lastSequence;
    return true;
}
#endif
