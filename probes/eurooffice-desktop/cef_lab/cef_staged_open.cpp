// GD-31J.6 isolated local DOCUMENT OPEN experiment. Never ship to users.
//
// Unlike the earlier empty-view smoke, this invokes the actual EuroOffice SDK
// CCefViewEditor::OpenLocalFile() with ONE SYNTHETIC OOXML fixture.
//
// SECURITY: a default upstream CEF build can disable sandboxing. Run ONLY in
// a separately approved, offline, disposable OS-isolated lab account/VM.
// Do not supply corporate documents, paths, storage mounts or credentials.
// Successful dispatch is NOT proof of document rendering, edit, writeback,
// PDF conversion or correct WORM handling.
#include <QApplication>
#include <QCryptographicHash>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QJsonDocument>
#include <QJsonObject>
#include <QLabel>
#include <QPointer>
#include <QTemporaryDir>
#include <QTimer>
#include <QVBoxLayout>
#include <QWidget>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <string>
#include "cefapplication.h"
#include "cefview.h"
#include "qascapplicationmanager.h"
#include "qcefview.h"

namespace {
// OnLoaded is a CEF-view event, NOT an Office file loaded/saved acknowledgement.
// Atomic because the upstream CEF callback's threading is not yet certified.
class SyntheticEditorView final : public QCefView {
public:
    using QCefView::QCefView;
    std::atomic<bool> cefViewLoaded{false};
    void OnLoaded() override {
        cefViewLoaded.store(true, std::memory_order_release);
        QCefView::OnLoaded();
    }
};

constexpr char kManifest[] = "gd31j6-fixture-manifest.json";
constexpr char kManifestSchema[] = "gd31j6-office-fixtures/1";

bool IsAllowedName(const QString& name) {
    return name == "gd31j6_writer.docx" ||
           name == "gd31j6_calc.xlsx" ||
           name == "gd31j6_impress.pptx";
}

bool PathIsLocalAndUnlinked(const QString& path) {
    if (!QDir::isAbsolutePath(path) || path.startsWith("//") ||
        path.startsWith("\\\\") || path.contains("://"))
        return false;
    QFileInfo info(path);
    // QFileInfo::isSymLink is a simple guard against linked files and dirs;
    // separately ensure the full canonical path remains beneath staging.
    if (info.isSymLink() || info.isSymbolicLink())
        return false;
    QString current = info.absolutePath();
    while (!current.isEmpty()) {
        QFileInfo parent(current);
        if (parent.isSymLink() || parent.isSymbolicLink())
            return false;
        const QString above = parent.dir().absolutePath();
        if (above == current) break;
        current = above;
    }
    return true;
}

bool VerifySyntheticFixture(const QString& stagingDir, const QString& fileName,
                            QString& path, QString& explanation) {
    if (!IsAllowedName(fileName) || !PathIsLocalAndUnlinked(stagingDir)) {
        explanation = "Unapproved fixture basename or staging root";
        return false;
    }
    const QFileInfo dir(stagingDir);
    if (!dir.isDir() || !dir.isReadable()) {
        explanation = "Local staging root missing";
        return false;
    }
    const QString canonicalRoot = dir.canonicalFilePath();
    const QString manifestPath = QDir(canonicalRoot).filePath(kManifest);
    path = QDir(canonicalRoot).filePath(fileName);
    if (!PathIsLocalAndUnlinked(manifestPath) ||
        !PathIsLocalAndUnlinked(path)) {
        explanation = "Linked fixture path";
        return false;
    }
    QFileInfo candidate(path);
    QFileInfo manifestInfo(manifestPath);
    if (!candidate.isFile() || !candidate.isReadable() ||
        !manifestInfo.isFile() || !manifestInfo.isReadable() ||
        candidate.size() <= 0 || candidate.size() > 128LL * 1024 * 1024 ||
        manifestInfo.size() <= 0 || manifestInfo.size() > 16 * 1024) {
        explanation = "Missing, oversized or unreadable synthetic fixture/manifest";
        return false;
    }
    if (candidate.canonicalPath() != canonicalRoot ||
        manifestInfo.canonicalPath() != canonicalRoot) {
        explanation = "Fixture escaped staging directory";
        return false;
    }

    QFile json(manifestPath);
    if (!json.open(QIODevice::ReadOnly)) return false;
    QJsonParseError error;
    const auto parsed = QJsonDocument::fromJson(json.readAll(), &error);
    if (error.error != QJsonParseError::NoError || !parsed.isObject()) return false;
    QJsonObject root = parsed.object();
    if (root.value("schema").toString() != kManifestSchema ||
        root.value("purpose").toString() !=
            "test-only-local-ooxml-no-corporate-documents" ||
        !root.value("files").isObject())
        return false;

    QJsonObject files = root.value("files").toObject();
    if (files.size() != 3 || !files.value(fileName).isObject()) return false;
    QJsonObject entry = files.value(fileName).toObject();
    if (entry.value("size").toDouble(-1) != candidate.size())
        return false;
    const QString expected = entry.value("sha256").toString();
    if (expected.size() != 64) return false;

    // Hash the actual bytes before passing the staged path into CEF. There is
    // a TOCTOU gap if other processes can write to staging: no production trust.
    QFile input(path);
    if (!input.open(QIODevice::ReadOnly)) return false;
    QCryptographicHash hasher(QCryptographicHash::Sha256);
    while (!input.atEnd()) {
        QByteArray buffer = input.read(65536);
        if (buffer.isEmpty()) return false;
        hasher.addData(buffer);
    }
    if (QString::fromLatin1(hasher.result().toHex()) != expected)
        return false;
    return true;
}

bool PrepareTemporaryProfile(QAscApplicationManager& manager, const QString& root) {
    QDir dirs;
    const QString cache = root + "/cache";
    const QString recovery = root + "/recovery";
    const QString plugins = root + "/plugins";
    const QString fonts = root + "/fonts";
    if (!dirs.mkpath(cache) || !dirs.mkpath(recovery) ||
        !dirs.mkpath(plugins) || !dirs.mkpath(fonts))
        return false;
    auto& settings = manager.m_oSettings;
    settings.SetUserDataPath(root.toStdWString());
    settings.cache_path = cache.toStdWString();
    settings.cookie_path = (root + "/cookies").toStdWString();
    settings.user_plugins_path = plugins.toStdWString();
    settings.system_plugins_path = plugins.toStdWString();
    settings.recover_path = recovery.toStdWString();
    settings.fonts_cache_info_path = fonts.toStdWString();
    settings.plugins_support = false;
    settings.macroses_support = false;
    settings.sign_support = false;
    return true;
}
} // namespace

int main(int argc, char** argv) {
    // Only one named synthetic fixture from an existing approved staging dir.
    // No URL, arbitrary filename, output path, save or print commands.
    if (argc != 4 || std::strcmp(argv[1], "--open-staged-lab") != 0)
        return 2;
    const QString staging = QString::fromLocal8Bit(argv[2]);
    const QString fixtureName = QString::fromLocal8Bit(argv[3]);
    QString path;
    QString reason;
    if (!VerifySyntheticFixture(staging, fixtureName, path, reason)) {
        std::fputs("GD31J6_REFUSED_INVALID_FIXTURE\n", stderr);
        return 3;
    }

    QApplication app(argc, argv);
    QTemporaryDir profile(QDir::tempPath() + "/get-document-gd31j6-XXXXXX");
    if (!profile.isValid()) return 4;
    QAscApplicationManager manager;
    if (!PrepareTemporaryProfile(manager, profile.path())) return 5;

    CApplicationCEF::Prepare(argc, argv);
    CApplicationCEF cef;
    const int started = cef.Init_CEF(&manager, argc, argv);
    if (cef.IsChromiumSubprocess())
        return started;
    if (started != 0)
        return 6;

    QWidget window;
    window.setWindowTitle(QString::fromUtf8("ГЭТ — синтетический документ EuroOffice"));
    window.resize(1024, 720);
    auto* layout = new QVBoxLayout(&window);
    auto* banner = new QLabel(QString::fromUtf8(
        "Эксперимент: только синтетические DOCX/XLSX/PPTX.\n"
        "Печать, WORM, корпоративные файлы и сервер запрещены."));
    banner->setWordWrap(true);
    layout->addWidget(banner);
    QPointer<SyntheticEditorView> view = new SyntheticEditorView(&window, QSize(980, 620));
    layout->addWidget(view);
    view->Create(&manager, cvwtEditor);
    if (!view || !view->GetCefView()) {
        manager.CloseApplication();
        return 7;
    }

    // Exact SDK API used in upstream CTabPanel::openLocalFile(...):
    // GetFileFormat(path) then OpenLocalFile(path, format, params).
    int format = CCefViewEditor::GetFileFormat(path.toStdWString());
    if (format == 0) {
        manager.CloseApplication();
        return 8;
    }
    auto* editor = static_cast<CCefViewEditor*>(view->GetCefView());
    editor->OpenLocalFile(path.toStdWString(), format, L"");
    const int viewId = editor->GetId();
    window.show();

    // No success acknowledgement is available yet. Timeout merely limits the
    // trial and does NOT claim the document loaded or saved successfully.
    QTimer::singleShot(15000, &app, &QCoreApplication::quit);
    const int exitCode = app.exec();
    if (view && viewId > 0)
        manager.DestroyCefView(viewId, false);
    // Capture callback state before manager tears down the native editor.
    // A browser callback does not prove OOXML was parsed/rendered correctly.
    const bool viewCallback = view && view->cefViewLoaded.load(std::memory_order_acquire);
    manager.CloseApplication();
    std::puts(viewCallback
        ? "GD31J6_CEF_VIEW_CALLBACK_OBSERVED_DOCUMENT_UNVERIFIED"
        : "GD31J6_OPEN_REQUEST_DISPATCHED_NOT_VERIFIED");
    return exitCode;
}
