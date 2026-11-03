/*
 * GD-31J.27be — STANDALONE, READ-ONLY LINUX EXPERIMENT.
 * Not part of GetDocument.Office, not a production recovery adapter.
 *
 * CLI: openat2_identity_probe <trusted-absolute-recovery-root> <64-hex-hash>
 *                               <guid-n>.docx|.xlsx|.pptx|.part.*
 *
 * Prints descriptor-derived device/inode numbers ONLY. Does not read ZIP
 * contents, write data, or authorize recovery. The root itself must be
 * trusted/provisioned securely: O_NOFOLLOW only protects its LAST component.
 *
 * The strict openat2 resolution is intentional: no openat or path fallback.
 * Cross-mount traversals fail closed, including nested mounted SMB volumes.
 * Do not enable the native adapter without ABI, OS and SMB tests.
 */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <inttypes.h>
#include <linux/openat2.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <unistd.h>

#define EXIT_UNSUPPORTED 77
#define MAX_RECOVERY_BYTES (128LL * 1024LL * 1024LL)

struct probe_id {
    uint64_t dev;
    uint64_t ino;
};

static int ascii_hex(char c)
{
    return (c >= '0' && c <= '9') ||
           (c >= 'a' && c <= 'f') ||
           (c >= 'A' && c <= 'F');
}

static int validate_folder_hash(const char *hash)
{
    if (hash == NULL || strlen(hash) != 64) return 0;
    for (size_t i = 0; i < 64; i++)
        if (!ascii_hex(hash[i])) return 0;
    return 1;
}

static int validate_copy_name(const char *name)
{
    if (name == NULL) return 0;
    size_t length = strlen(name);
    if (length < 37 || length > 48) return 0;
    for (size_t i = 0; i < 32; i++)
        if (!ascii_hex(name[i])) return 0;
    const char *suffix = name + 32;
    return strcmp(suffix, ".docx") == 0 ||
           strcmp(suffix, ".xlsx") == 0 ||
           strcmp(suffix, ".pptx") == 0 ||
           strcmp(suffix, ".part.docx") == 0 ||
           strcmp(suffix, ".part.xlsx") == 0 ||
           strcmp(suffix, ".part.pptx") == 0;
}

static int open_relative_strict(int dir_fd, const char *name, uint64_t flags)
{
#ifndef SYS_openat2
    (void)dir_fd;
    (void)name;
    (void)flags;
    errno = ENOSYS;
    return -1;
#else
    struct open_how how = {0};
    how.flags = flags | O_CLOEXEC | O_NOFOLLOW;
    how.mode = 0;
    how.resolve = RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS |
                  RESOLVE_NO_MAGICLINKS | RESOLVE_NO_XDEV;
    return (int)syscall(SYS_openat2, dir_fd, name, &how, sizeof(how));
#endif
}

static struct probe_id identity(const struct stat *s)
{
    struct probe_id id = {(uint64_t)s->st_dev, (uint64_t)s->st_ino};
    return id;
}

static int same_identity(const struct stat *first, const struct stat *second)
{
    struct probe_id a = identity(first);
    struct probe_id b = identity(second);
    return memcmp(&a, &b, sizeof(a)) == 0 && a.dev != 0 && a.ino != 0;
}

int main(int argc, char **argv)
{
    if (argc != 4 || argv[1][0] != '/' ||
        !validate_folder_hash(argv[2]) ||
        !validate_copy_name(argv[3])) {
        fprintf(stderr, "Usage: probe <trusted-absolute-root> <64-hex-hash> <guid-n>.docx|.xlsx|.pptx|.part.*\n");
        return 2;
    }

    int root_fd = -1;
    int folder_fd = -1;
    int copy_fd = -1;
    int status = 1;
    struct stat root_before, root_after, folder_before, folder_after;
    struct stat copy_before, copy_after;

    /* Bootstrap root trust is a deployment responsibility. For its own last
     * component, O_NOFOLLOW refuses symbolic links. No content is read. */
    root_fd = open(argv[1], O_PATH | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (root_fd < 0 || fstat(root_fd, &root_before) != 0 ||
        !S_ISDIR(root_before.st_mode)) {
        fprintf(stderr, "Trusted recovery root could not be opened or identified\n");
        goto cleanup;
    }

    folder_fd = open_relative_strict(root_fd, argv[2], O_PATH | O_DIRECTORY);
    if (folder_fd < 0) {
        if (errno == ENOSYS || errno == E2BIG) status = EXIT_UNSUPPORTED;
        fprintf(stderr, "Strict openat2 per-source directory open not available\n");
        goto cleanup;
    }
    if (fstat(folder_fd, &folder_before) != 0 ||
        !S_ISDIR(folder_before.st_mode) ||
        folder_before.st_dev != root_before.st_dev) {
        fprintf(stderr, "Recovery directory identity is unsupported\n");
        goto cleanup;
    }

    copy_fd = open_relative_strict(folder_fd, argv[3], O_RDONLY);
    if (copy_fd < 0) {
        if (errno == ENOSYS || errno == E2BIG) status = EXIT_UNSUPPORTED;
        fprintf(stderr, "Strict openat2 OOXML open not available\n");
        goto cleanup;
    }
    if (fstat(copy_fd, &copy_before) != 0 ||
        !S_ISREG(copy_before.st_mode) ||
        copy_before.st_dev != folder_before.st_dev ||
        copy_before.st_size <= 0 ||
        copy_before.st_size > MAX_RECOVERY_BYTES) {
        fprintf(stderr, "OOXML descriptor is not a bounded regular file\n");
        goto cleanup;
    }

    /* No file content read. Re-sample identities from the SAME descriptors. */
    if (fstat(root_fd, &root_after) != 0 ||
        fstat(folder_fd, &folder_after) != 0 ||
        fstat(copy_fd, &copy_after) != 0 ||
        !same_identity(&root_before, &root_after) ||
        !same_identity(&folder_before, &folder_after) ||
        !same_identity(&copy_before, &copy_after) ||
        copy_before.st_size != copy_after.st_size) {
        fprintf(stderr, "File or parent descriptor identity changed\n");
        goto cleanup;
    }

    struct probe_id folder_id = identity(&folder_after);
    struct probe_id file_id = identity(&copy_after);
    printf("DIAGNOSTIC ONLY: Linux descriptor identity, not recovery authorization\n");
    printf("parent dev=%" PRIu64 " ino=%" PRIu64 "\n", folder_id.dev, folder_id.ino);
    printf("copy   dev=%" PRIu64 " ino=%" PRIu64 "\n", file_id.dev, file_id.ino);
    status = 0;

cleanup:
    if (copy_fd >= 0) close(copy_fd);
    if (folder_fd >= 0) close(folder_fd);
    if (root_fd >= 0) close(root_fd);
    return status;
}
