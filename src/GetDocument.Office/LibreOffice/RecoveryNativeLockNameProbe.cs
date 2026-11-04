using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27bp: an optional READ-ONLY snapshot comparing the held lock file
/// with a fresh OS no-follow open of its current NAME. Neither this check
/// nor a matching file ID prevents an attacker from renaming the parent or
/// lock immediately afterwards. Not registered with production capture yet.
/// Parent must eventually be opened relative to an independently trusted
/// directory descriptor with verified writer isolation.
/// </summary>
internal static class RecoveryNativeLockNameProbe
{
    private const uint FileReadAttributes = 0x0080;
    private const uint ShareReadWriteDelete = 0x0007;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileAttributeTagInfoClass = 9;
    private const uint ReparsePointAttribute = 0x00000400;
    private const uint DirectoryAttribute = 0x00000010;
    // Linux O_PATH, O_NOFOLLOW and O_CLOEXEC (fixed UAPI bit values).
    private const int LinuxOPath = 0x200000;
    private const int LinuxONoFollow = 0x20000;
    private const int LinuxOCloexec = 0x80000;

    internal static RecoveryNativeFileId VerifyCurrentName(
        SafeFileHandle heldLockHandle, string lockPath)
    {
        ArgumentNullException.ThrowIfNull(heldLockHandle);
        if (heldLockHandle.IsClosed || heldLockHandle.IsInvalid)
            throw new ObjectDisposedException(nameof(heldLockHandle));
        if (string.IsNullOrWhiteSpace(lockPath) ||
            !Path.IsPathFullyQualified(lockPath))
            throw new ArgumentException(
                "Необходим абсолютный путь имени файла координации.", nameof(lockPath));

        RecoveryNativeFileId held =
            RecoveryNativeHandleIdProbe.ReadFromExistingHandle(heldLockHandle);
        using SafeFileHandle fresh = OpenNoFollowForMetadata(Path.GetFullPath(lockPath));
        RecoveryNativeFileId named =
            RecoveryNativeHandleIdProbe.ReadFromExistingHandle(fresh);
        if (held != named)
            throw new InvalidDataException(
                "Имя lock-файла больше не соответствует удерживаемому OS handle.");

        // Matched only at this instant; no ownership, ACL, directory pin or
        // native write/delete authority is implied by this return value.
        return held;
    }

    private static SafeFileHandle OpenNoFollowForMetadata(string fullPath)
    {
        if (OperatingSystem.IsWindows())
            return OpenWindowsNoFollow(fullPath);
        if (OperatingSystem.IsLinux())
            return OpenLinuxNoFollow(fullPath);
        throw new PlatformNotSupportedException(
            "Нативное no-follow открытие lock-файла не реализовано для ОС.");
    }

    private static SafeFileHandle OpenWindowsNoFollow(string fullPath)
    {
        SafeFileHandle handle = CreateFileW(fullPath, FileReadAttributes,
            ShareReadWriteDelete, IntPtr.Zero, OpenExisting,
            FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException("Windows не открыла lock-файл без перехода по ссылке.",
                new Win32Exception(error));
        }

        try
        {
            if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass,
                    out NativeFileAttributeTagInfo attrs, 8))
                throw new IOException("Windows не вернула атрибуты lock-файла.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            if ((attrs.FileAttributes & (ReparsePointAttribute | DirectoryAttribute)) != 0)
                throw new InvalidDataException(
                    "Lock-файл является ссылкой, каталогом или reparse point.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenLinuxNoFollow(string fullPath)
    {
        if (!BitConverter.IsLittleEndian ||
            RuntimeInformation.ProcessArchitecture is not (
                Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException(
                "Флаги Linux O_PATH/O_NOFOLLOW не аттестованы для архитектуры.");

        int fd;
        try
        {
            fd = OpenLinux(fullPath,
                LinuxOPath | LinuxONoFollow | LinuxOCloexec);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new PlatformNotSupportedException(
                "Нативное Linux open для lock-файла недоступно.", ex);
        }
        if (fd < 0)
            throw new IOException("Linux не открыл lock-файл без перехода по ссылке.",
                new Win32Exception(Marshal.GetLastPInvokeError()));

        // O_PATH|O_NOFOLLOW opens a symlink ITSELF; the handle-only statx
        // parser rejects the S_IFLNK type. This is for the FINAL component
        // only and does not defend against a mutable ancestor directory.
        var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        try
        {
            RecoveryNativeHandleIdProbe.ReadFromExistingHandle(handle);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx",
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle, int infoClass,
        out NativeFileAttributeTagInfo info, uint bufferBytes);

    [DllImport("libc", EntryPoint = "open", ExactSpelling = true,
        SetLastError = true)]
    private static extern int OpenLinux(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int flags);
}
