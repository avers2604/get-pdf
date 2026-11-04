using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27bp: read-only file identity from an ALREADY OPEN native handle.
/// Windows: FILE_ID_INFO (volume serial and full 128-bit file ID).
/// Linux: statx(AT_EMPTY_PATH) on the held descriptor, never a pathname.
/// This is a diagnostic primitive, NOT a no-follow opener, parent ownership
/// proof, kernel-lock claim, or authority to restore/unlink a file.
/// Not wired into production capture until NTFS/Astra platform gates pass.
/// </summary>
internal static class RecoveryNativeHandleIdProbe
{
    internal static int WindowsFileIdInfoBytes =>
        Marshal.SizeOf<WindowsFileIdInfo>();

    internal static bool WindowsUsesFileIdInfo => true;

    private const int FileIdInfoClass = 0x12;
    private const int AtEmptyPath = 0x1000;
    private const int AtSymlinkNoFollow = 0x0100;
    private const uint StatxType = 0x0001;
    private const uint StatxIno = 0x0100;
    private const uint StatxMntId = 0x1000;
    private const int StatxBufferBytes = 256;

    internal static RecoveryNativeFileId ReadFromExistingHandle(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsClosed || handle.IsInvalid)
            throw new ObjectDisposedException(nameof(handle),
                "Файловый дескриптор недействителен.");

        RecoveryNativeFileId identity;
        if (OperatingSystem.IsWindows())
            identity = ReadWindowsFileIdInfo(handle);
        else if (OperatingSystem.IsLinux())
            identity = ReadLinuxStatxHandle(handle);
        else
            throw new PlatformNotSupportedException(
                "Для данной платформы не определён проверяемый File ID.");

        if (!identity.IsComplete)
            throw new InvalidDataException(
                "Нативный идентификатор открытого файлового объекта неполон.");
        return identity;
    }

    private static RecoveryNativeFileId ReadWindowsFileIdInfo(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "FILE_ID_INFO поддерживается только на Windows.");
        if (!GetFileInformationByHandleEx(handle, FileIdInfoClass,
                out WindowsFileIdInfo info, (uint)WindowsFileIdInfoBytes))
            throw new IOException(
                "Не удалось получить FILE_ID_INFO из открытого дескриптора.",
                new Win32Exception(Marshal.GetLastPInvokeError()));

        return new RecoveryNativeFileId(
            RecoveryNativePlatform.Windows,
            info.VolumeSerialNumber,
            info.FileIdLow,
            info.FileIdHigh);
    }

    private static RecoveryNativeFileId ReadLinuxStatxHandle(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsLinux() || !BitConverter.IsLittleEndian ||
            RuntimeInformation.ProcessArchitecture is not (
                Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException(
                "Структура statx не аттестована для данной архитектуры.");

        // DangerousAddRef pins the fd while the native statx call executes.
        // The descriptor was opened by the CALLER; no filesystem path,
        // link target, or source document is opened or read here.
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            int fd = handle.DangerousGetHandle().ToInt32();
            var buffer = new byte[StatxBufferBytes];
            int rc;
            try
            {
                rc = Statx(fd, "", AtEmptyPath | AtSymlinkNoFollow,
                    StatxType | StatxIno | StatxMntId, buffer);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new PlatformNotSupportedException(
                    "libc statx отсутствует: аварийное восстановление требует проверки ОС.",
                    ex);
            }
            catch (DllNotFoundException ex)
            {
                throw new PlatformNotSupportedException(
                    "libc для чтения файлового идентификатора недоступна.", ex);
            }

            if (rc != 0)
                throw new IOException(
                    "statx(AT_EMPTY_PATH) не подтвердил идентификатор дескриптора.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));

            return ParseLinuxStatx(buffer);
        }
        finally
        {
            if (added) handle.DangerousRelease();
        }
    }

    /// <summary>
    /// Pure parser for the published 256-byte Linux little-endian statx UAPI.
    /// Parse only the fields needed for identification, and demand mount ID
    /// availability even though the existing RecoveryNativeFileId type
    /// carries only dev major/minor + inode. Mount IDs need a separate
    /// extended evidence model before native capture security can be claimed.
    /// </summary>
    internal static RecoveryNativeFileId ParseLinuxStatx(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length != StatxBufferBytes)
            throw new ArgumentException("Некорректная длина Linux statx.", nameof(buffer));

        var b = buffer.AsSpan();
        uint mask = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(0, 4));
        if ((mask & (StatxType | StatxIno | StatxMntId)) !=
            (StatxType | StatxIno | StatxMntId))
            throw new InvalidDataException(
                "Linux statx не вернул inode, тип файла или ID точки монтирования.");

        ushort mode = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(28, 2));
        // Only a normal file or a directory may be observed by this probe:
        // symbolic links, devices, FIFOs and sockets must not become lock IDs.
        const int FileTypeMask = 0xF000;
        const int RegularFile = 0x8000;
        const int DirectoryFile = 0x4000;
        int kind = mode & FileTypeMask;
        if (kind is not (RegularFile or DirectoryFile))
            throw new InvalidDataException("Дескриптор не является файлом или каталогом.");

        ulong inode = BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(32, 8));
        uint major = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(136, 4));
        uint minor = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(140, 4));
        ulong mountId = BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(144, 8));
        if (mountId == 0)
            throw new InvalidDataException(
                "Linux statx не подтвердил ID точки монтирования.");

        ulong device = ((ulong)major << 32) | minor;
        var result = new RecoveryNativeFileId(
            RecoveryNativePlatform.Linux, device, inode, 0);
        if (!result.IsComplete)
            throw new InvalidDataException(
                "Linux statx вернул пустой device/inode идентификатор.");
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileIdInfo
    {
        internal ulong VolumeSerialNumber;
        internal ulong FileIdLow;
        internal ulong FileIdHigh;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx",
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle, int fileInformationClass,
        out WindowsFileIdInfo info, uint bufferBytes);

    [DllImport("libc", EntryPoint = "statx", ExactSpelling = true,
        SetLastError = true)]
    private static extern int Statx(
        int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, [Out] byte[] info);
}
