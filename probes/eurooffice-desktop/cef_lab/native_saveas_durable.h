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
#include <linux/magic.h>
#include <string>
#include <sys/stat.h>
#include <sys/vfs.h>
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
    ~Fd() { closeNow(); }
    bool closeNow() noexcept {
        if (value < 0) return true;
        const int old = value;
        value = -1;
        return ::close(old) == 0;
    }
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
        !S_ISDIR(state.st_mode) || state.st_uid != ::geteuid() ||
        (state.st_mode & 0022))
        return Fd();
    // Duplicate without losing close-on-exec. No inherited directory fds
    // in the CEF renderer/utility subprocess.
    return Fd(::fcntl(dir.value, F_DUPFD_CLOEXEC, 0));
}

// The SDK's private scratch has no group/other access. Owner-identity and
// inode checks are necessary but NOT sufficient against another same-UID
// process, so never link the writable SDK inode as the published output.
inline bool IsOwnerPrivateScratch(int scratchFd) {
    struct stat state{};
    return ::fstat(scratchFd, &state) == 0 &&
           S_ISDIR(state.st_mode) && state.st_uid == ::geteuid() &&
           (state.st_mode & 0777) == 0700 && state.st_nlink >= 2;
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

inline bool ExactOwnedMarker(int directoryFd, const char* name,
                             const QByteArray& expected) {
    Fd marker(::openat(directoryFd, name,
                       O_RDONLY | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK));
    struct stat current{};
    if (!marker.ok() || ::fstat(marker.value, &current) != 0 ||
        !S_ISREG(current.st_mode) || current.st_nlink != 1 ||
        (current.st_mode & 0022) || current.st_size != expected.size() ||
        expected.size() > 64)
        return false;
    char buffer[64];
    const ssize_t bytes = ::read(marker.value, buffer, sizeof(buffer));
    return bytes == expected.size() &&
           QByteArray(buffer, bytes) == expected;
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

// GD31J27j: bind the hardlink to the exact OPEN verified inode, never to the
// source filename after validation. AT_EMPTY_PATH requires CAP_DAC_READ_SEARCH
// on some Linux kernels: ordinary disposable-VM users can instead use the
// procfs fd magic-link, following the documented linkat(2) approach.
// The fallback is permitted ONLY when /proc/self/fd is a real procfs mount.
// No path-based scratch/name fallback is allowed, including on older kernels.
// Return false on EEXIST, EACCES, unsupported or invalid procfs.
inline bool LinkVerifiedSourceFd(int sourceFd, int outputFd,
                                 const char* basename) {
    if (sourceFd < 0 || outputFd < 0 || !basename ||
        !SafeBasename(QString::fromLatin1(basename)))
        return false;
#ifdef AT_EMPTY_PATH
    if (::linkat(sourceFd, "", outputFd, basename, AT_EMPTY_PATH) == 0)
        return true;
    const int emptyPathError = errno;
    if (emptyPathError != EPERM && emptyPathError != EINVAL &&
        emptyPathError != ENOENT && emptyPathError != EOPNOTSUPP)
        return false; // In particular: EEXIST is final; never retry.
#endif
    Fd procFdDirectory(::open("/proc/self/fd",
                               O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC));
    struct statfs procfs{};
    if (!procFdDirectory.ok() ||
        ::fstatfs(procFdDirectory.value, &procfs) != 0 ||
        procfs.f_type != PROC_SUPER_MAGIC)
        return false; // A spoofable proc path cannot create a trusted link.
    const std::string fdName = std::to_string(sourceFd);
    return ::linkat(procFdDirectory.value, fdName.c_str(),
                    outputFd, basename, AT_SYMLINK_FOLLOW) == 0;
}

// GD31J27k: make a NEW unlinked snapshot in the private directory.
// The SDK's named output inode is NEVER published directly. The snapshot
// starts with st_nlink=0 and only the owner process holds its descriptor;
// after fchmod and closing the writable FD, publish via the read-only FD.
// This minimizes accidental SDK/same-UID writer access, NOT a security
// boundary against ptrace/procfd access from an untrusted same-UID process.
struct SnapShotFdOwnership {
    // Both descriptors must be O_CLOEXEC. None is passed to the SDK.
    // Failure at any step leaves no named snapshot to clean up.
};

inline bool SameStableMeta(const struct stat& before, const struct stat& after) {
    return SameFile(before, after) &&
           before.st_mtim.tv_sec == after.st_mtim.tv_sec &&
           before.st_mtim.tv_nsec == after.st_mtim.tv_nsec &&
           before.st_ctim.tv_sec == after.st_ctim.tv_sec &&
           before.st_ctim.tv_nsec == after.st_ctim.tv_nsec;
}

inline bool SnapshotSyntheticBytes(int sourceFd, int snapshotFd,
                                  const QString& expectedSha) {
    if (::lseek(sourceFd, 0, SEEK_SET) != 0) return false;
    struct stat first{}, last{}, target{};
    if (::fstat(sourceFd, &first) != 0 || !S_ISREG(first.st_mode) ||
        first.st_nlink != 1 || first.st_uid != ::geteuid() ||
        first.st_size <= 0 || first.st_size > 32LL * 1024 * 1024 ||
        ::fstat(snapshotFd, &target) != 0 || !S_ISREG(target.st_mode) ||
        target.st_nlink != 0 || target.st_uid != ::geteuid() ||
        target.st_size != 0)
        return false;
    QCryptographicHash hash(QCryptographicHash::Sha256);
    char block[64 * 1024];
    qint64 total = 0;
    for (;;) {
        const ssize_t got = ::read(sourceFd, block, sizeof(block));
        if (got < 0 && errno == EINTR) continue;
        if (got <= 0) {
            if (got < 0) return false;
            break;
        }
        total += got;
        if (total > first.st_size) return false;
        hash.addData(block, got);
        ssize_t offset = 0;
        while (offset < got) {
            const ssize_t put = ::write(snapshotFd, block + offset, got - offset);
            if (put < 0 && errno == EINTR) continue;
            if (put <= 0) return false;
            offset += put;
        }
    }
    return total == first.st_size &&
           QString::fromLatin1(hash.result().toHex()) == expectedSha &&
           ::fstat(sourceFd, &last) == 0 && SameStableMeta(first, last) &&
           ::fstat(snapshotFd, &target) == 0 &&
           target.st_size == first.st_size && target.st_nlink == 0;
}

// Open an independent O_RDONLY handle to the anonymous inode. /proc must
// genuinely be procfs, not a user-controlled symlink or arbitrary mount.
// We close the writable anonymous descriptor BEFORE creating a visible link.
inline Fd OpenSnapshotReadOnly(int writerFd) {
    Fd procFdDirectory(::open("/proc/self/fd",
                O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC));
    struct statfs procfs{};
    if (!procFdDirectory.ok() ||
        ::fstatfs(procFdDirectory.value, &procfs) != 0 ||
        procfs.f_type != PROC_SUPER_MAGIC)
        return Fd();
    const std::string fdName = std::to_string(writerFd);
    return Fd(::openat(procFdDirectory.value, fdName.c_str(),
                       O_RDONLY | O_CLOEXEC | O_NONBLOCK));
}

// Output durability sequence:
// validate source inode/content -> new anonymous snapshot -> close writable
// snapshot FD -> no-replace FD hardlink -> output directory fsync.
// A failed post-link check has UNKNOWN durability and must never unlink.
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
    if (!scratch.ok() || !output.ok() ||
        !IsOwnerPrivateScratch(scratch.value) ||
        !ExactOwnedMarker(scratch.value, PRIVATE_MARKER_NAME,
                          QByteArray(GD31J27G_PRIVATE_SCRATCH_ONLY)) ||
        !ExactOwnedMarker(output.value, "gd31j25-output-lab.marker",
                          QByteArray("GETDOC_GD31J25_DISPOSABLE_OUTPUT_ONLY\n")))
        return false;
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
        (before.st_mode & 0022) ||
        before.st_size <= 0 || before.st_size > 32LL * 1024 * 1024)
        return false;
    if (!StableFdDigest(source.value, expectedSha) ||
        !fsync_output(source.value))
        return false;

    struct stat latest{}, named{};
    if (::fstat(source.value, &latest) != 0 ||
        ::fstatat(scratch.value, name.constData(), &named,
                  AT_SYMLINK_NOFOLLOW) != 0 ||
        !SameFile(before, latest) || !SameFile(latest, named) ||
        before.st_mtim.tv_sec != latest.st_mtim.tv_sec ||
        before.st_mtim.tv_nsec != latest.st_mtim.tv_nsec ||
        before.st_ctim.tv_sec != latest.st_ctim.tv_sec ||
        before.st_ctim.tv_nsec != latest.st_ctim.tv_nsec)
        return false;
    // Revalidate the named SDK input before making a separate anonymous inode.
    // No user or renderer has a pathname to the future snapshot inode.
    Fd snapshotWriter(::openat(scratch.value, ".",
                    O_TMPFILE | O_RDWR | O_CLOEXEC, 0600));
    struct stat snapshotBefore{};
    if (!snapshotWriter.ok() ||
        !SnapshotSyntheticBytes(source.value, snapshotWriter.value, expectedSha) ||
        ::fstat(snapshotWriter.value, &snapshotBefore) != 0 ||
        !S_ISREG(snapshotBefore.st_mode) ||
        snapshotBefore.st_uid != ::geteuid() ||
        snapshotBefore.st_nlink != 0 ||
        ::fsync(snapshotWriter.value) != 0 ||
        ::fchmod(snapshotWriter.value, 0400) != 0 ||
        ::fsync(snapshotWriter.value) != 0)
        return false;

    Fd snapshotReader = OpenSnapshotReadOnly(snapshotWriter.value);
    struct stat snapshotOpened{}, sourceAfterCopy{};
    if (!snapshotReader.ok() ||
        ::fstat(snapshotReader.value, &snapshotOpened) != 0 ||
        !SameFile(snapshotBefore, snapshotOpened) ||
        snapshotOpened.st_nlink != 0 ||
        (snapshotOpened.st_mode & 0777) != 0400 ||
        !snapshotWriter.closeNow() ||
        !StableFdDigest(snapshotReader.value, expectedSha) ||
        ::fstat(source.value, &sourceAfterCopy) != 0 ||
        !SameStableMeta(latest, sourceAfterCopy) ||
        ::fstatat(scratch.value, name.constData(), &named,
                  AT_SYMLINK_NOFOLLOW) != 0 ||
        !SameFile(sourceAfterCopy, named))
        return false;

    // Publishing the writable SDK inode is prohibited. Only the copy opened
    // via O_RDONLY can acquire the ONE permanent final directory name.
    if (!LinkVerifiedSourceFd(snapshotReader.value, output.value,
                              name.constData()))
        return false;

    struct stat snapshotAfter{}, published{};
    if (::fstat(snapshotReader.value, &snapshotAfter) != 0 ||
        ::fstatat(output.value, name.constData(), &published,
                  AT_SYMLINK_NOFOLLOW) != 0 ||
        !SameFile(snapshotOpened, snapshotAfter) ||
        !SameFile(snapshotAfter, published) ||
        snapshotAfter.st_nlink != 1 || published.st_nlink != 1 ||
        !StableFdDigest(snapshotReader.value, expectedSha))
        return false; // Unknown durability; NEVER unlink final name.
    if (::fsync(output.value) != 0)
        return false; // Unknown durability; NEVER unlink final name.
    return true;
}
} // namespace Gd31J27G
