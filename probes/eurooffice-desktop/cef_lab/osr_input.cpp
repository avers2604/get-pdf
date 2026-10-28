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

OsrInputPump::OsrInputPump(QString sessionId, QString tabId, bool clicksApproved)
    : m_sessionId(std::move(sessionId)), m_tabId(std::move(tabId)),
      m_clicksApproved(clicksApproved) {}

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
    } else {
        // Keyboard, IME, clipboard, file paths, JS and direct print forbidden.
        return false;
    }
    ++m_lastSequence;
    return true;
}
#endif
