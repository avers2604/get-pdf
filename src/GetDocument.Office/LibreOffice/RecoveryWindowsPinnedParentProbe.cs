using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27bq: Windows read-only diagnostic pin for an existing directory.
/// FILE_FLAG_OPEN_REPARSE_POINT only prevents following the FINAL component.
/// Ancestors may still be renamed/reparsed and the path is NOT a trusted
/// anchor. A held handle and an owner SID are not permission to write/delete.
/// This module is NOT wired into the production recovery coordinator.
/// </summary>
internal static class RecoveryWindowsPinnedParentProbe
{
    private const uint ReadControl = 0x00020000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint ShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileAttributeTagInfoClass = 9;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;

    internal static RecoveryWindowsPinnedParentLease OpenForDiagnostics(
        string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) ||
            !Path.IsPathFullyQualified(directoryPath))
            throw new ArgumentException(
                "Для диагностики нужен полный абсолютный путь каталога.",
                nameof(directoryPath));
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Нативный дескриптор каталога поддерживается только на Windows.");

        string path = Path.GetFullPath(directoryPath);
        SafeFileHandle handle = CreateFileW(path,
            ReadControl | FileReadAttributes, ShareAll,
            0, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            int code = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException("Нельзя открыть каталожный handle для диагностики.",
                new Win32Exception(code));
        }

        try
        {
            if (!GetFileInformationByHandleEx(handle,
                    FileAttributeTagInfoClass, out FileAttributeTagInfo tag, 8))
                throw new IOException(
                    "Windows не вернула атрибуты открытого каталога.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            if ((tag.Attributes & FileAttributeDirectory) == 0 ||
                (tag.Attributes & FileAttributeReparsePoint) != 0)
                throw new InvalidDataException(
                    "Объект не является обычным каталогом без reparse point.");

            RecoveryWindowsParentEvidence evidence =
                RecoveryWindowsParentSecurityProbe.ObserveExistingHandle(handle);

            // OS handle stays pinned until lease.Dispose. No FileInfo-based
            // path re-check can establish immutable ancestry or safe ACEs.
            return new RecoveryWindowsPinnedParentLease(handle, evidence);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        internal uint Attributes;
        internal uint ReparseTag;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx",
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle, int informationClass,
        out FileAttributeTagInfo info, uint bufferBytes);
}

internal sealed class RecoveryWindowsPinnedParentLease : IDisposable
{
    private SafeFileHandle? _handle;
    internal RecoveryWindowsParentEvidence Security { get; }

    internal SafeFileHandle Handle =>
        _handle ?? throw new ObjectDisposedException(nameof(RecoveryWindowsPinnedParentLease));

    internal RecoveryWindowsPinnedParentLease(
        SafeFileHandle handle, RecoveryWindowsParentEvidence security)
    {
        _handle = handle;
        Security = security;
    }

    public void Dispose() =>
        Interlocked.Exchange(ref _handle, null)?.Dispose();
}
