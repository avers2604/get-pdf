#pragma once

// Wayland CEF synthetic-only pointer IPC. Clicks opt-in from GD-31J.15; no keyboard/IME.
// Only compiled when GETDOC_GD31J14_POINTER_INPUT=1 and on Linux.
#ifdef GETDOC_GD31J14_POINTER_INPUT
#include <QByteArray>
#include <QElapsedTimer>
#include <QString>

class QCefView;
class CCefViewEditor;

class OsrInputPump final {
public:
    OsrInputPump(QString sessionId, QString tabId, bool clicksApproved = false,
                 bool keyboardApproved = false);
    bool Enable();
    // Called ONLY from the Qt/CEF owner thread's QTimer, never OnPaint.
    bool Poll(QCefView* view, CCefViewEditor* editor);
private:
    bool Dispatch(const QByteArray& bytes, QCefView* view, CCefViewEditor* editor);
    QString m_sessionId;
    QString m_tabId;
    QByteArray m_pending;
    int m_lastSequence = 0;
    int m_rateCount = 0;
    bool m_focused = false;
    bool m_clicksApproved = false;
    bool m_keyboardApproved = false;
    bool m_leftDown = false;
    int m_clickCount = 1;
    QElapsedTimer m_rateWindow;
};
#endif
