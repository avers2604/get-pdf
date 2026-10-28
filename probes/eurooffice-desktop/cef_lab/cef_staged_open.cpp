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
#include <QUuid>
#include <QTimer>
#include <QVBoxLayout>
#include <QWidget>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <string>
#include "cefapplication.h"
#include "cefview.h"
#include "applicationmanager_events.h"
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

// Only aggregate allowlisted notifications from the EXACT synthetic editor.
// Keep callback data/paths/document text out of the diagnostic channel.
// SDK OnSave/PrintEnd is NOT proof of a completed revision or PDF export.
class SyntheticEventObserver final : public NSEditorApi::CAscCefMenuEventListener {
public:
    std::atomic<int> expectedViewId{-1};
    std::atomic<bool> modified{false};
    std::atomic<bool> onSave{false};
    std::atomic<bool> saveCancelled{false};
    std::atomic<bool> printEnd{false};

    bool IsSupportEvent(int type) override {
        return type == ASC_MENU_EVENT_TYPE_CEF_MODIFY_CHANGED ||
               type == ASC_MENU_EVENT_TYPE_CEF_ONSAVE ||
               type == ASC_MENU_EVENT_TYPE_CEF_PRINT_END;
    }

    void OnEvent(NSEditorApi::CAscCefMenuEvent* event) override {
        if (event && event->get_SenderId() == expectedViewId.load(std::memory_order_acquire)) {
            switch (event->m_nType) {
            case ASC_MENU_EVENT_TYPE_CEF_MODIFY_CHANGED:
                modified.store(true, std::memory_order_release);
                break;
            case ASC_MENU_EVENT_TYPE_CEF_ONSAVE:
                onSave.store(true, std::memory_order_release);
                if (event->m_pData) {
                    auto* data = static_cast<NSEditorApi::CAscDocumentOnSaveData*>(event->m_pData);
                    if (data->get_IsCancel())
                        saveCancelled.store(true, std::memory_order_release);
                }
                break;
            case ASC_MENU_EVENT_TYPE_CEF_PRINT_END:
                printEnd.store(true, std::memory_order_release);
                break;
            default:
                break;
            }
        }
        // The upstream listener takes ownership of the callback event.
        NSEditorApi::CAscCefMenuEventListener::OnEvent(event);
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
    if ((argc != 4 && argc != 6) ||
        std::strcmp(argv[1], "--open-staged-lab") != 0)
        return 2;
    // GD-31J.8 adds a ONE-WAY final snapshot envelope. Not a live editor
    // command channel: no save, print, document bytes or network access.
    QString sessionId;
    if (argc == 6) {
        if (std::strcmp(argv[4], "--ipc-session") != 0)
            return 2;
        sessionId = QString::fromLatin1(argv[5]);
        const QUuid parsed(sessionId);
        if (parsed.isNull() ||
            parsed.toString(QUuid::WithoutBraces) != sessionId)
            return 2;
    }
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

    SyntheticEventObserver eventObserver;
    manager.SetEventListener(&eventObserver);

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
    eventObserver.expectedViewId.store(viewId, std::memory_order_release);
    window.show();

    // No success acknowledgement is available yet. Timeout merely limits the
    // trial and does NOT claim the document loaded or saved successfully.
    QTimer::singleShot(15000, &app, &QCoreApplication::quit);
    const int exitCode = app.exec();
    // Snapshot BEFORE any CEF/Qt destroy callback can invalidate the widget.
    // A browser callback does not prove OOXML was parsed/rendered correctly.
    const bool viewCallback = view && view->cefViewLoaded.load(std::memory_order_acquire);
    if (view && viewId > 0)
        manager.DestroyCefView(viewId, false);
    manager.SetEventListener(nullptr);
    manager.CloseApplication();

    QJsonObject nativeEvents;
    nativeEvents["viewLoaded"] = viewCallback;
    nativeEvents["modifiedNotification"] = eventObserver.modified.load(std::memory_order_acquire);
    nativeEvents["onSaveNotification"] = eventObserver.onSave.load(std::memory_order_acquire);
    nativeEvents["onSaveCancelled"] = eventObserver.saveCancelled.load(std::memory_order_acquire);
    nativeEvents["printEndNotification"] = eventObserver.printEnd.load(std::memory_order_acquire);

    QJsonObject receipt;
    receipt["schema"] = "GD31J7_EVENTS_ONLY";
    receipt["nativeEvents"] = nativeEvents;
    receipt["documentVerified"] = false;
    receipt["saveVerified"] = false;
    receipt["pdfVerified"] = false;
    receipt["printApproved"] = false;
    receipt["laboratoryOnly"] = true;
    QJsonObject output;
    if (!sessionId.isEmpty()) {
        // Exact, single JSONL frame for the managed GD-31J.8 tab IPC pump.
        // It is emitted only AFTER the lab editor window has closed.
        QJsonObject ipc;
        ipc["protocol"] = "getdoc-eurodesktop-ipc/1";
        ipc["kind"] = "snapshot";
        ipc["sessionId"] = sessionId;
        ipc["sequence"] = 1;
        ipc["receipt"] = receipt;
        output = ipc;
    } else {
        // Legacy lab CLI retains its earlier marker + raw receipt behavior.
        std::puts(viewCallback
            ? "GD31J6_CEF_VIEW_CALLBACK_OBSERVED_DOCUMENT_UNVERIFIED"
            : "GD31J6_OPEN_REQUEST_DISPATCHED_NOT_VERIFIED");
        output = receipt;
    }
    const QByteArray serialized = QJsonDocument(output).toJson(QJsonDocument::Compact);
    std::fwrite(serialized.constData(), 1, static_cast<size_t>(serialized.size()), stdout);
    std::fputc('\n', stdout);
    if (!sessionId.isEmpty()) {
        // A final terminal frame makes the managed tab leave its active state.
        // This is NOT a document save/print confirmation.
        QJsonObject closed;
        closed["protocol"] = "getdoc-eurodesktop-ipc/1";
        closed["kind"] = "event";
        closed["sessionId"] = sessionId;
        closed["sequence"] = 2;
        closed["event"] = "closed";
        closed["eventCode"] = 2001;
        const QByteArray terminal = QJsonDocument(closed).toJson(QJsonDocument::Compact);
        std::fwrite(terminal.constData(), 1, static_cast<size_t>(terminal.size()), stdout);
        std::fputc('\n', stdout);
    }
    std::fflush(stdout);
    return exitCode;
}
