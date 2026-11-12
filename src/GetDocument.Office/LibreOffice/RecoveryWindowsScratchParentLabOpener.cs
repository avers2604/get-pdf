using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27ce: lab-only Windows parent directory opener which requests
/// FILE_ADD_FILE + FILE_TRAVERSE on the held directory handle, unlike the
/// earlier read-only pinned-parent probe. A requested/granted handle right
/// is NOT independent proof of safe effective ACL, ancestor chain, token,
/// integrity level, namespace or security policy. This is NOT a trusted root.
/// The SINGLE full pathname lookup is permitted only for the operator's
/// expressly configured test directory. No child pathname is ever opened.
/// </summary>
internal static class RecoveryWindowsScratchParentLabOpener
{
    private const uint FileAddFile = 0x00000002;
    private const uint FileTraverse = 0x00000020;
    private const uint FileReadAttributes = 0x00000080;
    private const uint ReadControl = 0x00020000;
    private const uint Synchronize = 0x00100000;
    private const uint ShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileAttributeTagInfoClass = 9;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;

    internal static uint RequiredAccessMask =>
        FileAddFile | FileTraverse | FileReadAttributes | ReadControl | Synchronize;
    internal static bool ProductionOpenerEnabled => false;
    internal static bool CanAuthorizeProductionRoot => false;
    internal static bool HasVerifiedAncestorChain => false;
    internal static bool HasVerifiedEffectiveAccess => false;
    internal static bool CanAuthorizeCleanup => false;
    internal static bool CanAuthorizeDeletion => false;
    internal static bool CanAuthorizePublication => false;

    internal static string ValidateLocalAbsoluteLabPath(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) ||
            !Path.IsPathFullyQualified(directory) ||
            directory.StartsWith(@"\\", StringComparison.Ordinal) ||
            directory.Contains('\0'))
            throw new ArgumentException(
                "Требуется абсолютный локальный путь к явно выбранной лаборатории.",
                nameof(directory));
        string canonical = Path.GetFullPath(directory);
        if (string.Equals(canonical, Path.GetPathRoot(canonical),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "Корневой каталог диска не подходит для лаборатории.",
                nameof(directory));
        return canonical;
    }

    internal static RecoveryWindowsScratchParentFinding Evaluate(
        RecoveryWindowsParentEvidence parent,
        RecoveryWindowsAclRiskEvidence acl,
        string expectedOwnerSid) =>
        RecoveryWindowsJournalScratchFactory.EvaluateParent(
            parent, acl, expectedOwnerSid);

    internal static RecoveryWindowsScratchLabParentLease OpenConfiguredLabDirectory(
        string directoryPath, CancellationToken cancellationToken = default)
    {
        string canonical = ValidateLocalAbsoluteLabPath(directoryPath);
        string configured = Environment.GetEnvironmentVariable(
            "GD31J27CE_PRIVATE_LAB_DIRECTORY") ?? "";
        if (!string.Equals(
                Environment.GetEnvironmentVariable("GD31J27CE_PARENT_LAB"),
                "1", StringComparison.Ordinal) ||
            !string.Equals(canonical, ValidateConfiguredPath(configured),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Не подтверждён отдельный ACL-qualified lab-каталог Windows.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Этот opener поддерживает только Windows.");
        SafeFileHandle handle = CreateFileW(canonical, RequiredAccessMask,
            ShareAll, 0, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException(
                "Windows отказала в FILE_ADD_FILE/FILE_TRAVERSE на тестовом каталоге.",
                new Win32Exception(error));
        }
        try
        {
            if (!GetFileInformationByHandleEx(handle,
                    FileAttributeTagInfoClass, out FileAttributeTagInfo tag, 8))
                throw new IOException("Не удалось прочитать Windows directory tag.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            if ((tag.Attributes & FileAttributeDirectory) == 0 ||
                (tag.Attributes & FileAttributeReparsePoint) != 0)
                throw new InvalidDataException(
                    "Лабораторный parent является reparse point или не каталогом.");

            var before = RecoveryWindowsParentSecurityProbe.ObserveExistingHandle(handle);
            var acl = RecoveryWindowsAclRiskObserver.ObserveExistingHandle(handle);
            if (Evaluate(before, acl, before.OwnerSid) !=
                RecoveryWindowsScratchParentFinding.ObservedButNotTrusted)
                throw new InvalidDataException(
                    "DACL/owner лабораторного каталога не прошла диагностику.");
            cancellationToken.ThrowIfCancellationRequested();
            // Handle owns the opened directory. No automatic cleanup.
            return new RecoveryWindowsScratchLabParentLease(handle,
                before, acl);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static string ValidateConfiguredPath(string configured)
    {
        try { return ValidateLocalAbsoluteLabPath(configured); }
        catch (ArgumentException) { return ""; }
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

/// <summary>
/// Only a caller-owned lab directory handle. No authority to treat this
/// handle as a trusted root or to delete any directory entry.
/// </summary>
internal sealed class RecoveryWindowsScratchLabParentLease : IDisposable
{
    private SafeFileHandle? _handle;
    internal RecoveryWindowsParentEvidence Security { get; }
    internal RecoveryWindowsAclRiskEvidence Acl { get; }

    internal RecoveryWindowsScratchLabParentLease(SafeFileHandle handle,
        RecoveryWindowsParentEvidence security,
        RecoveryWindowsAclRiskEvidence acl)
    {
        _handle = handle;
        Security = security;
        Acl = acl;
    }

    internal SafeFileHandle Handle =>
        _handle ?? throw new ObjectDisposedException(
            nameof(RecoveryWindowsScratchLabParentLease));
    internal bool HasTrustedAncestry => false;
    internal bool CanAuthorizePublication => false;
    internal bool CanAuthorizeDeletion => false;

    public void Dispose() =>
        Interlocked.Exchange(ref _handle, null)?.Dispose();
}
