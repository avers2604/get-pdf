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
#include <QBuffer>
#include <QImage>
#include <QElapsedTimer>
#include <QThread>
#include <QMutex>
#include <QMutexLocker>
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
#if defined(GETDOC_GD31J12_WAYLAND_OSR_CAPTURE) && defined(__linux__)
#include <cerrno>
#include <csignal>
#include <fcntl.h>
#include <unistd.h>
#endif
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

#ifdef GETDOC_GD31J12_WAYLAND_OSR_CAPTURE
    void EnableSyntheticOsrCapture(bool streaming = false) {
        m_streaming.store(streaming, std::memory_order_release);
        m_captureEnabled.store(true, std::memory_order_release);
    }

    // This callback is already present in the pinned EuroOffice SDK. The
    // upstream implementation calls QCefView::OnPaint for Wayland only; it
    // needs its own paint staging/GL swap. Never replace or re-enter it.
    void OnPaint(const void* buffer, int width, int height) override {
        QCefView::OnPaint(buffer, width, height);
        if (!m_captureEnabled.load(std::memory_order_acquire) ||
            (!m_streaming.load(std::memory_order_acquire) &&
             m_paintObserved.load(std::memory_order_acquire)) ||
            !IsWayland() || !buffer ||
            width < 1 || height < 1 || width > 4096 || height > 4096 ||
            static_cast<long long>(width) * height > 4'194'304)
            return;

        // CEF hands us temporary BGRA pixels, never an owned QImage. Create
        // a tiny independent deep copy *inside* the callback, without
        // Qt repaint/events/process IO or touching customer document paths.
        const auto* bytes = static_cast<const uchar*>(buffer);
        QImage view(bytes, width, height, QImage::Format_ARGB32_Premultiplied);
        QImage preview = view.scaled(256, 144, Qt::KeepAspectRatio,
                                     Qt::FastTransformation).copy();
        if (preview.isNull() || preview.width() < 1 || preview.height() < 1)
            return;
        QMutexLocker lock(&m_captureMutex);
        if (m_streaming.load(std::memory_order_relaxed)) {
            // Latest-frame-only single-slot buffer. If the pipe is busy we
            // overwrite the sample, never accumulate or block CEF OnPaint.
            m_latestFrame = preview;
            ++m_latestRevision;
            m_paintObserved.store(true, std::memory_order_release);
            return;
        }
        if (m_paintObserved.load(std::memory_order_relaxed))
            return;
        m_capture = preview;
        m_paintObserved.store(true, std::memory_order_release);
    }

    QImage SnapshotOsrLab() {
        QMutexLocker lock(&m_captureMutex);
        return m_capture.copy();
    }

    QImage TakeLatestStreamFrame(quint64& knownRevision) {
        QMutexLocker lock(&m_captureMutex);
        if (m_latestRevision == knownRevision || m_latestFrame.isNull())
            return QImage();
        knownRevision = m_latestRevision;
        return m_latestFrame.copy();
    }
private:
    std::atomic<bool> m_captureEnabled{false};
    std::atomic<bool> m_streaming{false};
    std::atomic<bool> m_paintObserved{false};
    QMutex m_captureMutex;
    QImage m_capture;
    QImage m_latestFrame;
    quint64 m_latestRevision = 0;
#endif
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
    const bool osrLab = argc == 4 &&
        std::strcmp(argv[1], "--open-staged-wayland-osr-lab") == 0;
    const bool ordinaryLab = argc == 4 &&
        std::strcmp(argv[1], "--open-staged-lab") == 0;
    if (!ordinaryLab && !osrLab) return 2;
#ifndef GETDOC_GD31J12_WAYLAND_OSR_CAPTURE
    if (osrLab) return 2;
#else
    // The current pinned SDK enables CEF windowless rendering ONLY on
    // QT_QPA_PLATFORM=wayland. Windows and X11 use SetAsChild native windows.
    // Do not change QT_QPA_PLATFORM here; require an approved OS lab setup.
    if (osrLab && qgetenv("QT_QPA_PLATFORM") != "wayland") return 9;
#endif
    const QString staging = QString::fromLocal8Bit(argv[2]);
    const QString fixtureName = QString::fromLocal8Bit(argv[3]);
    QString path;
    QString reason;
    if (!VerifySyntheticFixture(staging, fixtureName, path, reason)) {
        std::fputs("GD31J6_REFUSED_INVALID_FIXTURE\n", stderr);
        return 3;
    }

    QApplication app(argc, argv);
#ifdef GETDOC_GD31J12_WAYLAND_OSR_CAPTURE
    if (osrLab && app.platformName() != "wayland") return 9;
#endif
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
#ifdef GETDOC_GD31J12_WAYLAND_OSR_CAPTURE
    if (osrLab) view->EnableSyntheticOsrCapture();
#endif
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

#ifdef GETDOC_GD31J12_WAYLAND_OSR_CAPTURE
    // EuroOffice's Linux CEF path uses CefDoMessageLoopWork(). A plain Qt
    // app.exec() does not necessarily pump Chromium: schedule CEF work on the
    // Qt owner thread, NEVER from OnPaint itself (reentrancy is unsafe).
    QTimer cefWorkPump;
    if (osrLab) {
        QObject::connect(&cefWorkPump, &QTimer::timeout, &app,
                         [&cef]() { cef.DoMessageLoopEvent(); });
        cefWorkPump.start(10);
    }
#endif

    // No success acknowledgement is available yet. Timeout merely limits the
    // trial and does NOT claim the document loaded or saved successfully.
    QTimer::singleShot(15000, &app, &QCoreApplication::quit);
    const int exitCode = app.exec();
#ifdef GETDOC_GD31J12_WAYLAND_OSR_CAPTURE
    cefWorkPump.stop();
#endif
    // Snapshot BEFORE any CEF/Qt destroy callback can invalidate the widget.
    // A browser callback does not prove OOXML was parsed/rendered correctly.
    const bool viewCallback = view && view->cefViewLoaded.load(std::memory_order_acquire);
#ifdef GETDOC_GD31J12_WAYLAND_OSR_CAPTURE
    // Copy image before manager destroys CEF's widget. This sample is only
    // the first received OnPaint frame, not proof of complete OOXML import.
    const QImage osrSnapshot = (osrLab && view) ? view->SnapshotOsrLab() : QImage();
#endif
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
    // Preserve earlier diagnostic markers but never claim OOXML import.
    std::puts(viewCallback
        ? "GD31J6_CEF_VIEW_CALLBACK_OBSERVED_DOCUMENT_UNVERIFIED"
        : "GD31J6_OPEN_REQUEST_DISPATCHED_NOT_VERIFIED");
    const QByteArray serialized = QJsonDocument(receipt).toJson(QJsonDocument::Compact);
    std::fwrite(serialized.constData(), 1, static_cast<size_t>(serialized.size()), stdout);
    std::fputc('\n', stdout);
#ifdef GETDOC_GD31J12_WAYLAND_OSR_CAPTURE
    if (osrLab) {
        QByteArray imageBytes;
        if (!osrSnapshot.isNull()) {
            QBuffer output(&imageBytes);
            if (!output.open(QIODevice::WriteOnly) ||
                !osrSnapshot.save(&output, "PNG") ||
                imageBytes.size() > 48 * 1024)
                imageBytes.clear();
        }
        QJsonObject receipt;
        receipt["schema"] = "getdoc-cef-osr-lab/1";
        receipt["source"] = "cef-osr-wayland-synthetic-only";
        receipt["paintCallbackObserved"] = !osrSnapshot.isNull();
        receipt["frameAvailable"] = !imageBytes.isEmpty();
        receipt["pixelWidth"] = imageBytes.isEmpty() ? 0 : osrSnapshot.width();
        receipt["pixelHeight"] = imageBytes.isEmpty() ? 0 : osrSnapshot.height();
        receipt["payloadBase64"] = QString::fromLatin1(imageBytes.toBase64());
        receipt["sha256"] = imageBytes.isEmpty() ? QString() : QString::fromLatin1(
            QCryptographicHash::hash(imageBytes, QCryptographicHash::Sha256).toHex());
        // CEF OnPaint is not a successful, verified document import.
        receipt["documentVerified"] = false;
        receipt["saveVerified"] = false;
        receipt["pdfVerified"] = false;
        receipt["printApproved"] = false;
        receipt["laboratoryOnly"] = true;
        std::puts("GD31J12_OSR_CAPTURE_UNVERIFIED");
        const QByteArray json = QJsonDocument(receipt).toJson(QJsonDocument::Compact);
        std::fwrite(json.constData(), 1, static_cast<size_t>(json.size()), stdout);
        std::fputc('\n', stdout);
    }
#endif
    return exitCode;
}
