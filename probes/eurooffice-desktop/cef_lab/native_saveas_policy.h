// GD-31J.26 Linux-only, disposable *synthetic* native SaveAs policy.
// Included ONLY when GETDOC_GD31J26_NATIVE_SAVEAS_LAB is explicitly built.
// Native SDK may run unsandboxed: OS isolation and readonly corporate mounts
// are operator requirements, not properties of this header.
#pragma once
#include <QCryptographicHash>
#include <QDir>
#include <QFile>
#include <QFileDevice>
#include <QFileInfo>
#include <QTemporaryDir>
#include <QString>
#include <memory>
#include <unistd.h>

namespace Gd31J26 {
struct NativeSaveAsStaging {
    QString originalPath;
    QString originalSha256;
    QString shadowPath;
    QString nativeOutputPath;
    QString finalOutputPath;
    QString approvedOutputRoot;
    std::unique_ptr<QTemporaryDir> scratch;
};

inline QString NativeOutputBasename(const QString& fixture) {
    if (fixture == "gd31j6_writer.docx") return "gd31j25_writer_saved.docx";
    if (fixture == "gd31j6_calc.xlsx") return "gd31j25_calc_saved.xlsx";
    if (fixture == "gd31j6_impress.pptx") return "gd31j25_impress_saved.pptx";
    return {};
}

// No symlink in any existing component. Files never received from a user path.
inline bool NoLinksInPath(const QString& raw) {
    if (!QDir::isAbsolutePath(raw) || raw.startsWith("//") ||
        raw.startsWith("\\\\") || raw.contains("://")) return false;
    QString path = QDir::cleanPath(raw);
    QFileInfo item(path);
    if (item.isSymLink() || item.isSymbolicLink()) return false;
    while (path != "/") {
        const QFileInfo current(path);
        if (current.isSymLink() || current.isSymbolicLink()) return false;
        const QString parent = current.dir().absolutePath();
        if (parent == path) return false;
        path = parent;
    }
    return true;
}

inline QString LabSourceSha256(const QString& path) {
    if (!NoLinksInPath(path)) return {};
    const QFileInfo before(path);
    if (!before.isFile() || !before.isReadable() || before.isSymLink() ||
        before.size() <= 0 || before.size() > 128LL * 1024 * 1024) return {};
    QFile source(path);
    if (!source.open(QIODevice::ReadOnly)) return {};
    QCryptographicHash sha(QCryptographicHash::Sha256);
    qint64 count = 0;
    while (!source.atEnd()) {
        const QByteArray chunk = source.read(64 * 1024);
        if (chunk.isEmpty()) return {};
        count += chunk.size();
        if (count > 128LL * 1024 * 1024) return {};
        sha.addData(chunk);
    }
    const QFileInfo after(path);
    if (count != before.size() || after.size() != before.size() ||
        after.lastModified() != before.lastModified()) return {};
    return QString::fromLatin1(sha.result().toHex());
}

// Source is never handed to CEF: the SDK opens the disposable shadow.
// Existing output is refused (never a replacement); private scratch is
// created in the same FS as final output for atomic linkat-style publication.
inline bool PrepareSyntheticShadow(const QString& sourcePath,
                                   const QString& stagingRoot,
                                   const QString& fixture,
                                   NativeSaveAsStaging& out) {
    const QString basename = NativeOutputBasename(fixture);
    if (basename.isEmpty() || !NoLinksInPath(sourcePath) ||
        !NoLinksInPath(stagingRoot)) return false;
    const QString outputRoot = QDir(stagingRoot).filePath("gd31j25-output");
    const QFileInfo dir(outputRoot);
    if (!dir.isDir() || dir.isSymLink() || !NoLinksInPath(outputRoot) ||
        (dir.permissions() & (QFileDevice::WriteGroup | QFileDevice::WriteOther)))
        return false;
    const QString marker = QDir(outputRoot).filePath("gd31j25-output-lab.marker");
    QFile markerFile(marker);
    if (!NoLinksInPath(marker) || !markerFile.open(QIODevice::ReadOnly) ||
        markerFile.size() != 38 || // exact prefix+LF, separately compare bytes
        markerFile.readAll() != "GETDOC_GD31J25_DISPOSABLE_OUTPUT_ONLY\n") return false;
    const QString finalPath = QDir(outputRoot).filePath(basename);
    if (QFileInfo::exists(finalPath) || QFileInfo(finalPath).isSymLink()) return false;
    const QString sha = LabSourceSha256(sourcePath);
    if (sha.isEmpty()) return false;
    auto scratch = std::make_unique<QTemporaryDir>(
        QDir(outputRoot).filePath("gd31j26-private-XXXXXX"));
    if (!scratch->isValid() || !NoLinksInPath(scratch->path())) return false;
    const QString shadowPath = QDir(scratch->path()).filePath(fixture);
    if (!QFile::copy(sourcePath, shadowPath) ||
        LabSourceSha256(sourcePath) != sha ||
        LabSourceSha256(shadowPath) != sha) return false;
    out.originalPath = sourcePath;
    out.originalSha256 = sha;
    out.shadowPath = shadowPath;
    out.nativeOutputPath = QDir(scratch->path()).filePath(basename);
    out.finalOutputPath = finalPath;
    out.approvedOutputRoot = outputRoot;
    out.scratch = std::move(scratch);
    return true;
}

// Should be called ONLY after the native editor and its CEF view are closed.
// 'link' atomically fails if final filename already exists; unlike QFile::copy,
// it cannot replace another output, even under a cooperative race.
inline bool NativeSaveAsPublish(const NativeSaveAsStaging& stage,
                               bool nativeCallbackCompleted,
                               QString& publishedSha) {
    publishedSha.clear();
    if (!nativeCallbackCompleted || !stage.scratch ||
        !NoLinksInPath(stage.nativeOutputPath) ||
        !NoLinksInPath(stage.finalOutputPath) ||
        !NoLinksInPath(stage.originalPath) ||
        LabSourceSha256(stage.originalPath) != stage.originalSha256 ||
        QFileInfo::exists(stage.finalOutputPath) ||
        QFileInfo(stage.finalOutputPath).isSymLink())
        return false;

    const QFileInfo temp(stage.nativeOutputPath);
    if (!temp.isFile() || temp.isSymLink() || !temp.isReadable() ||
        temp.size() <= 0 || temp.size() > 32LL * 1024 * 1024) return false;
    const QString digest = LabSourceSha256(stage.nativeOutputPath);
    if (digest.isEmpty() || LabSourceSha256(stage.nativeOutputPath) != digest)
        return false;
    const QByteArray from = QFile::encodeName(stage.nativeOutputPath);
    const QByteArray to = QFile::encodeName(stage.finalOutputPath);
    if (::link(from.constData(), to.constData()) != 0) return false;
    // The new inode was published exclusively. Verify it again; if integrity
    // unexpectedly fails, remove ONLY the just-created synthetic lab link.
    if (LabSourceSha256(stage.finalOutputPath) != digest ||
        LabSourceSha256(stage.originalPath) != stage.originalSha256) {
        ::unlink(to.constData());
        return false;
    }
    publishedSha = digest;
    return true;
}
} // namespace Gd31J26
