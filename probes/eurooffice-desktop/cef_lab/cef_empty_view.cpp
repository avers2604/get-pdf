// GD-31J.4 -- isolated lifecycle test of a SINGLE EMPTY EuroOffice CEF editor view.
// No work files, WORM originals, document URL, save, export, print or native
// window embedding into GET Desktop. Do not run outside an isolated/offline lab.
//
// NOTE: Upstream CEF may compile with no_sandbox=true. Never grant this process
// access to employee data, a network share, credentials, or the GET workspace.
#include <QApplication>
#include <QDir>
#include <QLabel>
#include <QPointer>
#include <QTemporaryDir>
#include <QTimer>
#include <QVBoxLayout>
#include <QWidget>
#include <cstdio>
#include <cstring>
#include <string>

#include "cefapplication.h"
#include "cefview.h"
#include "qascapplicationmanager.h"
#include "qcefview.h"

namespace {
bool ConfigureTemporaryProfile(QAscApplicationManager& manager, const QString& root)
{
    const QString cache = root + "/cache";
    const QString recovery = root + "/recovery";
    const QString plugins = root + "/plugins";
    const QString fonts = root + "/fonts";
    QDir dir;
    if (!dir.mkpath(cache) || !dir.mkpath(recovery) ||
        !dir.mkpath(plugins) || !dir.mkpath(fonts))
        return false;

    auto& s = manager.m_oSettings;
    s.SetUserDataPath(root.toStdWString());
    s.cache_path = cache.toStdWString();
    s.cookie_path = (root + "/cookies").toStdWString();
    s.recover_path = recovery.toStdWString();
    s.user_plugins_path = plugins.toStdWString();
    s.system_plugins_path = plugins.toStdWString();
    s.fonts_cache_info_path = fonts.toStdWString();
    s.macroses_support = false;
    s.plugins_support = false;
    s.sign_support = false;
    return true;
}
} // namespace

int main(int argc, char** argv)
{
    // Do not accept files, URLs, --type, --no-sandbox or arbitrary CEF flags.
    // Child Chromium processes belong to the vendor-supplied editors_helper.
    if (argc != 2 || std::strcmp(argv[1], "--cef-empty-view-lab") != 0)
        return 2;

    QApplication app(argc, argv);
    QTemporaryDir profile("get-document-empty-cef-view-XXXXXX");
    if (!profile.isValid())
        return 3;

    QAscApplicationManager manager;
    if (!ConfigureTemporaryProfile(manager, profile.path()))
        return 4;

    CApplicationCEF::Prepare(argc, argv);
    CApplicationCEF cef;
    const int initialized = cef.Init_CEF(&manager, argc, argv);
    if (cef.IsChromiumSubprocess())
        return initialized;
    if (initialized != 0)
        return 5;

    QWidget window;
    window.setWindowTitle(QString::fromUtf8("ГЭТ — лаборатория пустого CEF"));
    window.resize(960, 640);
    auto* layout = new QVBoxLayout(&window);

    auto* notice = new QLabel(QString::fromUtf8(
        "Эксперимент EuroOffice CEF: только пустой редакторный вид.\n"
        "Открытие, сохранение, печать и работа с документами ЗАПРЕЩЕНЫ."));
    notice->setWordWrap(true);
    layout->addWidget(notice);

    // Upstream QCefView::Create(cvwtEditor) ALREADY invokes
    // CAscApplicationManager::CreateCefEditor(this). Never call it a second time.
    QPointer<QCefView> view = new QCefView(&window, QSize(900, 480));
    layout->addWidget(view);
    view->Create(&manager, cvwtEditor);
    if (!view || !view->GetCefView()) {
        manager.CloseApplication();
        return 6;
    }

    const int viewId = view->GetCefView()->GetId();
    window.show();

    // Bound the runtime even if CEF never draws. No file/URL commands are sent.
    // A successful exit is NOT proof the editor rendered or functioned.
    QTimer::singleShot(12000, &app, &QCoreApplication::quit);
    const int exitCode = app.exec();

    // Shutdown in the same GUI thread: release native view via manager,
    // then close remaining CEF state before Qt widgets destruct.
    if (view && view->GetCefView() && viewId > 0)
        manager.DestroyCefView(viewId, false);
    manager.CloseApplication();
    std::puts("GD31J4_EMPTY_VIEW_LIFECYCLE_ONLY");
    return exitCode;
}
