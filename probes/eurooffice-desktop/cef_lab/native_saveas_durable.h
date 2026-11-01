// GD31J27g: durable, *synthetic-only* exclusive native SaveAs publication.
// Only included by the isolated Linux/Wayland CEF lab. Never use for user
// files, WORM, SMB, real SaveAs, or production transactions.
// Unknown durability after linkat MUST NOT trigger automatic unlink/overwrite.
#pragma once
#include <QByteArray>
#include <QCryptographicHash>
#include <QFile>
#include <QString>
#include <cerrno>
#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

namespace Gd31J27G {
inline constexpr char PRIVATE_MARKER_NAME[] = "gd31j27g-private-owner.marker";
inline constexpr char GD31J27G_PRIVATE_SCRATCH_ONLY[] =
    "GETDOC_GD31J27G_PRIVATE_SCRATCH_ONLY\n";

struct Fd {
    int value{-1};
    explicit Fd(int raw = -1) noexcept : value(raw) {}
    Fd(const Fd&) = delete;
    Fd& operator=(const Fd&) = delete;
    ~Fd() { if (value >= 0) ::close(value); }
    bool ok() const noexcept { return value >= 0; }
};

inline bool SafeBasename(const QString& name) {
    return name == "gd31j25_writer_saved.docx" ||
           name == "gd31j25_calc_saved.xlsx" ||
           name == "gd31j25_impress_saved.pptx";
}

inline Fd OpenPrivateDir(const QString& directory) {
    // Caller additionally checks all existing components with NoLinksInPath.
    const QByteArray bytes = QFile::encodeName(directory);
    Fd dir(::open(bytes.constData(), O_RDONLY | O_DIRECTORY |
                  O_CLOEXEC | O_NOFOLLOW));
    struct stat state{};
    if (!dir.ok() || ::fstat(dir.value, &state) != 0 ||
        !S_ISDIR(state.st_mode) || (state.st_mode & 0022))
        return Fd();
    // Fd is deliberately noncopyable: cannot return dir by value unless
    // its file descriptor ownership is transferred explicitly.
    return Fd(::dup(dir.value));
}

inline bool fsync_directory(const QString& directory) {
    Fd dir = OpenPrivateDir(directory);
    return dir.ok() && ::fsync(dir.value) == 0;
}

inline bool SyncScratchParent(const QString& root) {
    return fsync_directory(root);
}

inline bool CreateScratchMarker(const QString& privatePath) {
    Fd directory = OpenPrivateDir(privatePath);
    if (!directory.ok()) return false;
    Fd marker(::openat(directory.value, PRIVATE_MARKER_NAME,
                       O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC | O_NOFOLLOW,
                       0600));
    if (!marker.ok()) return false;
    const QByteArray content(GD31J27G_PRIVATE_SCRATCH_ONLY);
    ssize_t count = ::write(marker.value, content.constData(),
                            static_cast<size_t>(content.size()));
    return count == content.size() && ::fsync(marker.value) == 0 &&
           ::fsync(directory.value) == 0;
}

inline bool SameFile(const struct stat& left, const struct stat& right) {
    return left.st_dev == right.st_dev && left.st_ino == right.st_ino &&
           left.st_size == right.st_size && S_ISREG(right.st_mode);
}

inline bool StableFdDigest(int fd, const QString& digest) {
    if (::lseek(fd, 0, SEEK_SET) < 0) return false;
    struct stat first{}, last{};
    if (::fstat(fd, &first) != 0 || !S_ISREG(first.st_mode) ||
        first.st_size <= 0 || first.st_size > 32LL * 1024 * 1024)
        return false;
    QCryptographicHash sha(QCryptographicHash::Sha256);
    char buffer[64 * 1024];
    off_t size = 0;
    for (;;) {
        const ssize_t readCount = ::read(fd, buffer, sizeof(buffer));
        if (readCount < 0 && errno == EINTR) continue;
        if (readCount < 0) return false;
        if (readCount == 0) break;
        size += readCount;
        if (size > first.st_size) return false;
        sha.addData(buffer, readCount);
    }
    if (::fstat(fd, &last) != 0 || size != first.st_size ||
        !SameFile(first, last) ||
        first.st_mtim.tv_sec != last.st_mtim.tv_sec ||
        first.st_mtim.tv_nsec != last.st_mtim.tv_nsec ||
        first.st_ctim.tv_sec != last.st_ctim.tv_sec ||
        first.st_ctim.tv_nsec != last.st_ctim.tv_nsec)
        return false;
    return QString::fromLatin1(sha.result().toHex()) == digest;
}

inline bool fsync_output(int fd) {
    return ::fsync(fd) == 0;
}

// Output durability sequence:
// validate exact source inode+content -> fsync(file) -> linkat with
// no-replace -> confirm SAME inode at both names -> fsync(final directory).
// If linkat succeeds but later checks/fsync fail, return false with
// UNKNOWN DURABILITY. Do NOT unlink: another actor could have changed the
// directory entry; unilateral rollback could delete a file we do not own.
inline bool DurableExclusivePublish(const QString& scratchDir,
                                   const QString& outputRoot,
                                   const QString& basename,
                                   const QString& expectedSha) {
    if (!SafeBasename(basename) || expectedSha.size() != 64) return false;
    Fd scratch = OpenPrivateDir(scratchDir);
    Fd output = OpenPrivateDir(outputRoot);
    if (!scratch.ok() || !output.ok()) return false;
    struct stat fromDir{}, toDir{};
    if (::fstat(scratch.value, &fromDir) != 0 ||
        ::fstat(output.value, &toDir) != 0 ||
        fromDir.st_dev != toDir.st_dev)
        return false; // cross-device link is disallowed; never copy as fallback.

    const QByteArray name = QFile::encodeName(basename);
    Fd source(::openat(scratch.value, name.constData(),
                       O_RDONLY | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK));
    struct stat before{};
    if (!source.ok() || ::fstat(source.value, &before) != 0 ||
        !S_ISREG(before.st_mode) || before.st_nlink != 1 ||
        before.st_size <= 0 || before.st_size > 32LL * 1024 * 1024)
        return false;
    if (!StableFdDigest(source.value, expectedSha) ||
        !fsync_output(source.value))
        return false;

    struct stat latest{}, named{};
    if (::fstat(source.value, &latest) != 0 ||
        ::fstatat(scratch.value, name.constData(), &named,
                  AT_SYMLINK_NOFOLLOW) != 0 ||
        !SameFile(before, latest) || !SameFile(latest, named))
        return false;
    // Exclusive hardlink: EEXIST means another file won. No replace.
    if (::linkat(scratch.value, name.constData(),
                 output.value, name.constData(), 0) != 0)
        return false;
    struct stat published{};
    if (::fstatat(output.value, name.constData(), &published,
                  AT_SYMLINK_NOFOLLOW) != 0 ||
        !SameFile(latest, published))
        return false; // unknown durability; final may exist, NEVER unlink.
    if (::fsync(output.value) != 0)
        return false; // unknown durability; final may exist, NEVER unlink.
    return true;
}
} // namespace Gd31J27G
