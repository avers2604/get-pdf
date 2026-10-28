// GD-31J.3 -- isolated, lab-only CEF INITIALIZATION smoke test.
// Requires the separately approved, pinned EuroOffice SDK libraries and a
// disposable, network-isolated QA host. It NEVER loads documents, invokes
// OpenLocalFile, spawns a print task or creates a CEF editor window.
//
// IMPORTANT: Upstream cefapplication.cpp conditionally uses no_sandbox=true
// unless CEF_USE_SANDBOX is defined. Never use sensitive files on this host.
// This probe is NOT linked into GET Documents desktop or its installer.
#include <QApplication>
#include <QDir>
#include <QTemporaryDir>
#include <QString>
#include <cstring>
#include <cstdio>
#include <string>
#include "cefapplication.h"
#include "qascapplicationmanager.h"

namespace {
// CEF writes cache/profile data during Init_CEF. Point the manager to an
// isolated temporary profile rather than the employee's or GET user's profile.
bool ConfigureIsolatedSettings(QAscApplicationManager& manager, const QString& profile) {
    QDir directories;
    const QString cache = profile + "/cache";
    const QString plugins = profile + "/plugins";
    const QString recovery = profile + "/recovery";
    const QString fonts = profile + "/fonts";
    if (!directories.mkpath(cache) || !directories.mkpath(plugins) ||
        !directories.mkpath(recovery) || !directories.mkpath(fonts))
        return false;

    auto& settings = manager.m_oSettings;
    settings.SetUserDataPath(profile.toStdWString());
    settings.cache_path = cache.toStdWString();
    settings.cookie_path = (profile + "/cookies").toStdWString();
    settings.user_plugins_path = plugins.toStdWString();
    settings.system_plugins_path = plugins.toStdWString();
    settings.recover_path = recovery.toStdWString();
    settings.fonts_cache_info_path = fonts.toStdWString();
    // No document path, remote storage, fonts download, network login,
    // macro/plugin installation or persistent user profile is supplied.
    settings.macroses_support = false;
    settings.plugins_support = false;
    settings.sign_support = false;
    return true;
}
} // namespace

int main(int argc, char** argv) {
    // The IT-owned runner controls this exact mode. No other commands exist,
    // including path, URL, enable-edit, print, or shell escape parameters.
    if (argc != 2 || std::strcmp(argv[1], "--cef-init-lab") != 0)
        return 2;

    // Keep the same relative order as the pinned official desktop entry point:
    // QApplication, CApplicationCEF::Prepare, Init_CEF, shutdown.
    QApplication app(argc, argv);
    QTemporaryDir profile("get-document-cef-init-XXXXXX");
    if (!profile.isValid())
        return 3;
    QAscApplicationManager manager;
    if (!ConfigureIsolatedSettings(manager, profile.path()))
        return 4;

    CApplicationCEF::Prepare(argc, argv);
    CApplicationCEF cef;
    const int code = cef.Init_CEF(&manager, argc, argv);
    if (cef.IsChromiumSubprocess())
        return code;
    if (code != 0)
        return 5;
    // No QWidget, QCefView, CefDocument or editor is ever created.
    manager.CloseApplication();
    cef.Close();
    // CEF init returned successfully. NOT proof of real editor API, security,
    // sandboxing, document roundtrip, event handling or shutdown stability.
    std::puts("GD31J3_CEF_INITIALIZATION_ONLY");
    return 0;
}
