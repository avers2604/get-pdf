// GD-31J.2 isolated Qt window/lifecycle probe for EuroOffice DesktopEditors.
// No file access, no CEF initialization, no actual Office editor and no save.
// This is NOT a production editor nor a trusted server or an SDK replacement.
// Uses a strict, version-pinned, one-line JSON handshake over stdio.
#include <QApplication>
#include <QCoreApplication>
#include <QLabel>
#include <QVBoxLayout>
#include <QWidget>
#include <QByteArray>
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
} // namespace

int main(int argc, char** argv) {
    if (argc != 2) return 2;
    const std::string mode(argv[1]);
    if (mode == "--headless-probe") {
        QCoreApplication application(argc, argv);
        if (!ReadStrictHello() || !ReplyHello()) return 2;
        return 0;
    }
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
