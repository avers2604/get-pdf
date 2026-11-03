/*
 * GD-31J.27be — STANDALONE, READ-ONLY WINDOWS FILE ID EXPERIMENT.
 * DIAGNOSTIC ONLY — NOT HANDLE-RELATIVE; NOT a safe recovery opener.
 *
 * Usage: fileid_identity_probe.exe <trusted-absolute-recovery-root>
 *                                 <64-hex-folder> <guid-n>.docx|.xlsx|.pptx|.part.*
 *
 * This probe obtains full 128-bit FILE_ID_INFO from actual Win32 HANDLES,
 * including their volume serial numbers, but its CreateFileW paths remain
 * pathname-based. FILE_FLAG_OPEN_REPARSE_POINT protects only the FINAL
 * path component, not intermediate junctions/symlinks or an atomic
 * directory-handle-relative open. Never enable the recovery native adapter
 * from this probe; security requires an independently reviewed solution.
 * No document contents are read, restored, written, or deleted.
 */
#define WIN32_LEAN_AND_MEAN
#define UNICODE
#define _UNICODE
#include <windows.h>
#include <winbase.h>
#include <strsafe.h>
#include <stdio.h>
#include <wchar.h>
#include <string.h>

#define EXIT_UNSUPPORTED 77
#define MAX_RECOVERY_BYTES (128LL * 1024LL * 1024LL)
#define MAX_PROBE_PATH 4096

static int ascii_hex(WCHAR c)
{
    return (c >= L'0' && c <= L'9') ||
           (c >= L'a' && c <= L'f') ||
           (c >= L'A' && c <= L'F');
}

static int validate_folder_hash(const WCHAR *hash)
{
    if (hash == NULL || wcslen(hash) != 64) return 0;
    for (size_t i = 0; i < 64; i++)
        if (!ascii_hex(hash[i])) return 0;
    return 1;
}

static int validate_copy_name(const WCHAR *name)
{
    if (name == NULL) return 0;
    size_t length = wcslen(name);
    if (length < 37 || length > 48) return 0;
    for (size_t i = 0; i < 32; i++)
        if (!ascii_hex(name[i])) return 0;
    const WCHAR *suffix = name + 32;
    return wcscmp(suffix, L".docx") == 0 ||
           wcscmp(suffix, L".xlsx") == 0 ||
           wcscmp(suffix, L".pptx") == 0 ||
           wcscmp(suffix, L".part.docx") == 0 ||
           wcscmp(suffix, L".part.xlsx") == 0 ||
           wcscmp(suffix, L".part.pptx") == 0;
}

static int absolute_root(const WCHAR *root)
{
    if (root == NULL) return 0;
    size_t len = wcslen(root);
    return (len >= 3 && root[1] == L':' &&
            (root[2] == L'\\' || root[2] == L'/')) ||
           (len >= 3 && root[0] == L'\\' && root[1] == L'\\');
}

static HANDLE open_identity_handle(const WCHAR *path)
{
    /* FILE_READ_ATTRIBUTES only; neither FILE_READ_DATA nor write access. */
    return CreateFileW(path, FILE_READ_ATTRIBUTES,
                       FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                       NULL, OPEN_EXISTING,
                       FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS,
                       NULL);
}

static int read_identity(HANDLE handle, int expect_directory,
                         FILE_ID_INFO *id, LARGE_INTEGER *length)
{
    FILE_ATTRIBUTE_TAG_INFO tag = {0};
    FILE_STANDARD_INFO standard = {0};
    if (!GetFileInformationByHandleEx(
            handle, FileAttributeTagInfo, &tag, sizeof(tag)))
        return 0;
    if ((tag.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        return 0;
    if (((tag.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) !=
        (expect_directory != 0))
        return 0;
    if (!GetFileInformationByHandleEx(handle, FileIdInfo, id, sizeof(*id)))
        return 0;
    if (!GetFileInformationByHandleEx(
            handle, FileStandardInfo, &standard, sizeof(standard)))
        return 0;
    if (id->VolumeSerialNumber == 0)
        return 0;
    int nonzero = 0;
    for (size_t i = 0; i < sizeof(id->FileId.Identifier); i++)
        nonzero |= id->FileId.Identifier[i];
    if (!nonzero) return 0;  /* Unsupported/partial network identity. */
    *length = standard.EndOfFile;
    return 1;
}

static int same_file_id(const FILE_ID_INFO *a, const FILE_ID_INFO *b)
{
    return a->VolumeSerialNumber == b->VolumeSerialNumber &&
           memcmp(a->FileId.Identifier, b->FileId.Identifier,
                  sizeof(a->FileId.Identifier)) == 0;
}

static void print_id(const WCHAR *label, const FILE_ID_INFO *id)
{
    wprintf(L"%ls volume=%llu id=", label,
            (unsigned long long)id->VolumeSerialNumber);
    for (size_t i = 0; i < sizeof(id->FileId.Identifier); i++)
        wprintf(L"%02X", (unsigned)id->FileId.Identifier[i]);
    wprintf(L"\n");
}

int wmain(int argc, WCHAR **argv)
{
    if (argc != 4 || !absolute_root(argv[1]) ||
        !validate_folder_hash(argv[2]) ||
        !validate_copy_name(argv[3])) {
        fwprintf(stderr, L"Usage: probe <trusted-absolute-root> <64-hex-hash> <guid-n>.docx|.xlsx|.pptx|.part.*\n");
        return 2;
    }

    WCHAR folder_path[MAX_PROBE_PATH] = {0};
    WCHAR copy_path[MAX_PROBE_PATH] = {0};
    if (FAILED(StringCchPrintfW(folder_path, MAX_PROBE_PATH,
                                L"%ls\\%ls", argv[1], argv[2])) ||
        FAILED(StringCchPrintfW(copy_path, MAX_PROBE_PATH,
                                L"%ls\\%ls", folder_path, argv[3]))) {
        fwprintf(stderr, L"Path exceeds diagnostic buffer limit\n");
        return 2;
    }

    HANDLE root_handle = INVALID_HANDLE_VALUE;
    HANDLE folder_handle = INVALID_HANDLE_VALUE;
    HANDLE copy_handle = INVALID_HANDLE_VALUE;
    int status = 1;
    FILE_ID_INFO root_before = {0}, root_after = {0};
    FILE_ID_INFO parent_before = {0}, parent_after = {0};
    FILE_ID_INFO copy_before = {0}, copy_after = {0};
    LARGE_INTEGER root_length, folder_length, copy_length, ignored;

    root_handle = open_identity_handle(argv[1]);
    if (root_handle == INVALID_HANDLE_VALUE ||
        !read_identity(root_handle, 1, &root_before, &root_length)) {
        fwprintf(stderr, L"Trusted root identity unavailable\n");
        goto cleanup;
    }

    folder_handle = open_identity_handle(folder_path);
    if (folder_handle == INVALID_HANDLE_VALUE ||
        !read_identity(folder_handle, 1, &parent_before, &folder_length)) {
        fwprintf(stderr, L"Parent directory identity unavailable\n");
        goto cleanup;
    }

    copy_handle = open_identity_handle(copy_path);
    if (copy_handle == INVALID_HANDLE_VALUE ||
        !read_identity(copy_handle, 0, &copy_before, &copy_length) ||
        copy_length.QuadPart <= 0 ||
        copy_length.QuadPart > MAX_RECOVERY_BYTES) {
        fwprintf(stderr, L"OOXML identity missing or file out of bounds\n");
        goto cleanup;
    }

    /* Re-query identity on SAME handles. Does NOT prove all path components
     * were rooted relative to the original root HANDLE. */
    if (!read_identity(root_handle, 1, &root_after, &ignored) ||
        !read_identity(folder_handle, 1, &parent_after, &ignored) ||
        !read_identity(copy_handle, 0, &copy_after, &ignored) ||
        !same_file_id(&root_before, &root_after) ||
        !same_file_id(&parent_before, &parent_after) ||
        !same_file_id(&copy_before, &copy_after) ||
        root_before.VolumeSerialNumber != parent_before.VolumeSerialNumber ||
        parent_before.VolumeSerialNumber != copy_before.VolumeSerialNumber) {
        fwprintf(stderr, L"File/parent volume or identity changed\n");
        goto cleanup;
    }

    wprintf(L"DIAGNOSTIC ONLY — NOT HANDLE-RELATIVE, NOT recovery authorization\n");
    print_id(L"parent", &parent_after);
    print_id(L"copy  ", &copy_after);
    status = 0;

cleanup:
    if (copy_handle != INVALID_HANDLE_VALUE) CloseHandle(copy_handle);
    if (folder_handle != INVALID_HANDLE_VALUE) CloseHandle(folder_handle);
    if (root_handle != INVALID_HANDLE_VALUE) CloseHandle(root_handle);
    return status;
}
