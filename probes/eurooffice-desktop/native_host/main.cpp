// GD-31J.2 isolated Qt window/lifecycle probe for EuroOffice DesktopEditors.
// No file access, no CEF initialization, no actual Office editor and no save.
// This is NOT a production editor nor a trusted server or an SDK replacement.
// Uses a strict, version-pinned, one-line JSON handshake over stdio.
#include <QApplication>
#include <QCoreApplication>
#include <QLabel>
#include <QVBoxLayout>
#include <QWidget>
#include "ipc_probe.h"
#include <QByteArray>
#include <QEvent>
#include <QGuiApplication>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QScreen>
#include <QTimer>
#include <QWindow>
#include <cstdio>
#include <string>

namespace {
constexpr int kMaxRequest = 16 * 1024;
constexpr char kExpectedHello[] =
    "{\"command\":\"hello\",\"protocol\":\"getdoc-eurodesktop-probe/1\","
    "\"upstreamCommit\":\"6a671bb1ad238c61965a64de3a005c420940c814\"}";
constexpr char kHelloReply[] =
    "{\"protocol\":\"getdoc-eurodesktop-probe/1\","
    "\"engine\":\"EuroOfficeDesktopEditors\","
    "\"upstreamCommit\":\"6a671bb1ad238c61965a64de3a005c420940c814\","
    "\"features\":[]}";

bool ReadStrictHello() {
    std::string request;
    request.reserve(256);
    for (int i = 0; i <= kMaxRequest; ++i) {
        int ch = std::fgetc(stdin);
        if (ch == EOF) return false;
        if (ch == '\n')
            return request == kExpectedHello;
        if (i == kMaxRequest || ch == '\r' || ch == 0)
            return false;
        request.push_back(static_cast<char>(ch));
    }
    return false;
}

bool ReplyHello() {
    if (std::fputs(kHelloReply, stdout) < 0 ||
        std::fputc('\n', stdout) == EOF)
        return false;
    return std::fflush(stdout) == 0;
}
// Qt-only diagnostic telemetry. There are no device IDs, screenshots,
// document names, CEF handles or credentials in this report.
class WindowEventCounters final : public QObject {
public:
    int show = 0;
    int resize = 0;
    int move = 0;
    int focus = 0;
protected:
    bool eventFilter(QObject* watched, QEvent* event) override {
        Q_UNUSED(watched);
        switch (event->type()) {
        case QEvent::Show: ++show; break;
        case QEvent::Resize: ++resize; break;
        case QEvent::Move: ++move; break;
        case QEvent::FocusIn:
        case QEvent::WindowActivate: ++focus; break;
        default: break;
        }
        return false;
    }
};

int RunDisplayProbe(int argc, char** argv, bool manual) {
    QApplication application(argc, argv);
    application.setQuitOnLastWindowClosed(false);

    QWidget window;
    window.setWindowTitle(QString::fromUtf8(manual
        ? "ГЭТ — перетащите диагностическое окно на второй монитор"
        : "ГЭТ — тест масштабирования Qt (без CEF)"));
    WindowEventCounters counters;
    window.installEventFilter(&counters);
    window.resize(800, 500);
    if (manual) {
        auto* layout = new QVBoxLayout(&window);
        auto* notice = new QLabel(QString::fromUtf8(
            "Только Qt: переместите окно на второй экран и верните обратно.\\n"
            "Затем проверьте изменение масштаба системы (если разрешено).\\n"
            "Нет CEF, редактора, печати и доступа к документам.\\n"
            "Окно закроется автоматически через 30 секунд."));
        notice->setWordWrap(true);
        layout->addWidget(notice);
    }
    window.show();

    int screenChanges = 0;
    int dpiChanges = 0;
    for (QScreen* screen : QGuiApplication::screens()) {
        if (!screen) continue;
        QObject::connect(screen, &QScreen::logicalDotsPerInchChanged,
                         &application, [&dpiChanges](qreal) { ++dpiChanges; });
    }
    if (QWindow* handle = window.windowHandle()) {
        QObject::connect(handle, &QWindow::screenChanged, &application,
                         [&screenChanges](QScreen*) { ++screenChanges; });
    }

    // Exercise a second physical-widget size with a real Qt event loop.
    // This is NOT a user-visible CEF editor, even if running on Windows/Astra.
    QTimer::singleShot(70, &window, [&window]() { window.resize(1024, 640); });
    QTimer::singleShot(manual ? 30000 : 250, &application, [&]() {
        QJsonArray screens;
        for (QScreen* screen : QGuiApplication::screens()) {
            if (!screen) continue;
            const QRect size = screen->geometry();
            QJsonObject data;
            data["width"] = size.width();
            data["height"] = size.height();
            data["devicePixelRatio"] = screen->devicePixelRatio();
            data["logicalDpi"] = screen->logicalDotsPerInch();
            screens.append(data);
        }
        window.close();
        QJsonObject events;
        events["show"] = counters.show;
        events["resize"] = counters.resize;
        events["move"] = counters.move;
        events["focus"] = counters.focus;
        events["screenChanged"] = screenChanges;
        events["dpiChanged"] = dpiChanges;

        QJsonObject report;
        report["protocol"] = "getdoc-qt-display-diagnostics/1";
        report["runtimeKind"] = "qt-only-no-cef";
        report["cefInitialized"] = false;
        report["manualMode"] = manual;
        // Never claim physical multi-monitor correctness based on this probe.
        report["realMonitorVerified"] = false;
        report["screenCount"] = screens.size();
        report["screens"] = screens;
        report["windowEvents"] = events;
        report["closed"] = !window.isVisible();
        const QByteArray bytes = QJsonDocument(report).toJson(QJsonDocument::Compact);
        std::fwrite(bytes.constData(), 1, static_cast<size_t>(bytes.size()), stdout);
        std::fputc('\n', stdout);
        std::fflush(stdout);
        application.quit();
    });
    return application.exec();
}
} // namespace

int main(int argc, char** argv) {
    if (argc != 2) return 2;
    const std::string mode(argv[1]);
    if (mode == "--headless-probe") {
        QCoreApplication application(argc, argv);
        if (!ReadStrictHello() || !ReplyHello()) return 2;
        return 0;
    }
    if (mode == "--display-probe")
        return RunDisplayProbe(argc, argv, false);
    if (mode == "--display-live")
        return RunDisplayProbe(argc, argv, true);
    if (mode != "--gui-probe") return 2;

    // GUI is an OS-level Qt window only; intentionally DOES NOT initialize
    // Chromium or load document bytes. Native embedding in an Avalonia tab
    // requires a separate vetted process/window ownership design.
    QApplication application(argc, argv);
    if (!ReadStrictHello() || !ReplyHello()) return 2;

    QWidget window;
    window.setWindowTitle(QString::fromUtf8("ГЭТ Документы — проверка Qt"));
    window.setMinimumSize(640, 400);
    auto* layout = new QVBoxLayout(&window);
    auto* label = new QLabel(QString::fromUtf8(
        "EuroOffice DesktopEditors — диагностический Qt-стенд.\n"
        "Редактирование, сохранение, совместная работа и печать отключены.\n"
        "CEF/SDK не подключены. Служебные документы сюда не передавать."));
    label->setWordWrap(true);
    layout->addWidget(label);
    window.show();
    return application.exec();
}
