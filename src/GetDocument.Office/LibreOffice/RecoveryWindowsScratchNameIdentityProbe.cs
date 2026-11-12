using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27cd: lab-only READ-ONLY NtOpenFile with a caller-held
/// OBJECT_ATTRIBUTES.RootDirectory. Double name-open against a held scratch
/// descriptor. A matching ID cannot prevent a rename immediately afterwards.
/// The CC creator uses ShareAccess=0 BY DEFAULT: Windows may return
/// STATUS_SHARING_VIOLATION until the creator is closed. CE's separate
/// FILE_SHARE_READ laboratory mode can permit read-only observers, but
/// compatibility is NOT independently verified for NTFS/SMB.
/// A sharing failure is UNAVAILABLE observation, never proof.
/// No production binding to OfficeRecoveryStore and no mutation capability.
/// </summary>
internal static class RecoveryWindowsScratchNameIdentityProbe
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint ReadControl = 0x00020000;
    private const uint Synchronize = 0x00100000;
    private const uint FileShareAll = 0x00000007;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint ObjCaseInsensitive = 0x00000040;
    private const uint ObjDontReparse = 0x00001000;
    private const int FileAttributeTagInfoClass = 9;
    private const int StatusSharingViolation = unchecked((int)0xC0000043);

    internal static bool ProductionNameAuthorityEnabled => false;
    internal static bool CanAuthorizeCleanup => false;
    internal static bool CanAuthorizePublication => false;
    internal static bool CreatorSharePolicyAllowsReopen => false;

    internal static RecoveryWindowsScratchNameOpenFinding ClassifyNtOpenStatus(
        int status) => status switch
    {
        0 => RecoveryWindowsScratchNameOpenFinding.OpenSucceededUntrusted,
        StatusSharingViolation =>
            RecoveryWindowsScratchNameOpenFinding.ExclusiveShareDenied,
        _ => RecoveryWindowsScratchNameOpenFinding.OpenFailed
    };

    internal static RecoveryWindowsScratchNameFinding Evaluate(
        RecoveryWindowsScratchNameEvidence evidence)
    {
        var ids = new[]
        {
            evidence.ParentBefore, evidence.ParentAfter,
            evidence.HeldBefore, evidence.NamedFirst,
            evidence.NamedSecond, evidence.HeldAfter
        };
        if (ids.Any(x => !x.IsComplete ||
                          x.Platform != RecoveryNativePlatform.Windows))
            return RecoveryWindowsScratchNameFinding.IncompleteEvidence;
        if (evidence.ParentBefore != evidence.ParentAfter)
            return RecoveryWindowsScratchNameFinding.ParentChanged;
        if (evidence.FirstReparse || evidence.SecondReparse)
            return RecoveryWindowsScratchNameFinding.ReparseOrWrongType;
        if (ids.Any(x =>
                x.DeviceOrVolume != evidence.ParentBefore.DeviceOrVolume) ||
            evidence.HeldBefore == evidence.ParentBefore)
            return RecoveryWindowsScratchNameFinding.CrossVolumeOrAlias;
        if (evidence.HeldBefore != evidence.NamedFirst ||
            evidence.HeldBefore != evidence.NamedSecond ||
            evidence.HeldBefore != evidence.HeldAfter)
            return RecoveryWindowsScratchNameFinding.NameDoesNotMatchHeld;
        return RecoveryWindowsScratchNameFinding.MatchedAtObservation;
    }

    internal static RecoveryWindowsScratchNameEvidence ObserveExisting(
        SafeFileHandle heldParent, SafeFileHandle heldFile, string basename,
        CancellationToken cancellationToken = default)
    {
        RecoveryWindowsJournalScratchFactory.ValidateName(basename);
        ValidateHandle(heldParent);
        ValidateHandle(heldFile);
        if (!string.Equals(
                Environment.GetEnvironmentVariable("GD31J27CD_NATIVE_LAB"),
                "1", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Windows name-reopen допускается только на одобренном NTFS-стенде.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() ||
            RuntimeInformation.ProcessArchitecture is not
                (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException(
                "NtOpenFile RootDirectory поддерживается только Windows x64/arm64.");

        bool parentPinned = false, filePinned = false;
        try
        {
            heldParent.DangerousAddRef(ref parentPinned);
            heldFile.DangerousAddRef(ref filePinned);

            RecoveryWindowsParentEvidence before =
                RecoveryWindowsParentSecurityProbe.ObserveExistingHandle(heldParent);
            RecoveryWindowsAclRiskEvidence beforeAcl =
                RecoveryWindowsAclRiskObserver.ObserveExistingHandle(heldParent);
            if (RecoveryWindowsJournalScratchFactory.EvaluateParent(
                    before, beforeAcl, before.OwnerSid) !=
                RecoveryWindowsScratchParentFinding.ObservedButNotTrusted)
                throw new InvalidDataException("Родительская DACL/owner не подтверждена.");

            RecoveryNativeFileId heldBefore =
                ObserveNormalFileId(heldFile, before.OwnerSid);
            cancellationToken.ThrowIfCancellationRequested();

            using SafeFileHandle first = OpenNameReadOnly(heldParent, basename);
            RecoveryNativeFileId namedFirst =
                ObserveNormalFileId(first, before.OwnerSid);
            cancellationToken.ThrowIfCancellationRequested();

            using SafeFileHandle second = OpenNameReadOnly(heldParent, basename);
            RecoveryNativeFileId namedSecond =
                ObserveNormalFileId(second, before.OwnerSid);
            RecoveryNativeFileId heldAfter =
                ObserveNormalFileId(heldFile, before.OwnerSid);
            RecoveryWindowsParentEvidence after =
                RecoveryWindowsParentSecurityProbe.ObserveExistingHandle(heldParent);
            RecoveryWindowsAclRiskEvidence afterAcl =
                RecoveryWindowsAclRiskObserver.ObserveExistingHandle(heldParent);
            if (after.OwnerSid != before.OwnerSid ||
                !RecoveryWindowsJournalScratchFactory.SameAclSnapshot(
                    beforeAcl, afterAcl) ||
                RecoveryWindowsJournalScratchFactory.EvaluateParent(
                    after, afterAcl, before.OwnerSid) !=
                    RecoveryWindowsScratchParentFinding.ObservedButNotTrusted)
                throw new InvalidDataException(
                    "Owner SID или DACL родителя изменились между чтениями.");

            var evidence = new RecoveryWindowsScratchNameEvidence(
                before.FileId, after.FileId, heldBefore, namedFirst,
                namedSecond, heldAfter, FirstReparse: false,
                SecondReparse: false);
            if (Evaluate(evidence) !=
                    RecoveryWindowsScratchNameFinding.MatchedAtObservation)
                throw new InvalidDataException(
                    "NT pathname не совпадает с удерживаемым дескриптором.");
            cancellationToken.ThrowIfCancellationRequested();
            return evidence;
        }
        finally
        {
            if (filePinned) heldFile.DangerousRelease();
            if (parentPinned) heldParent.DangerousRelease();
        }
    }

    private static RecoveryNativeFileId ObserveNormalFileId(
        SafeFileHandle handle, string expectedOwnerSid)
    {
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass,
                out FileAttributeTagInfo info, 8))
            throw new IOException("Windows не вернула FILE_ATTRIBUTE_TAG_INFO.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        if (RecoveryWindowsJournalScratchFactory.EvaluateTag(
                info.Attributes, wantDirectory: false) !=
            RecoveryWindowsScratchTagFinding.NormalTypeButUntrusted)
            throw new InvalidDataException(
                "Имя ведёт к reparse point, каталогу или другому типу объекта.");

        RecoveryWindowsParentEvidence security =
            RecoveryWindowsParentSecurityProbe.ObserveExistingHandle(handle);
        var acl = RecoveryWindowsAclRiskObserver.ObserveExistingHandle(handle);
        if (RecoveryWindowsJournalScratchFactory.EvaluateParent(
                security, acl, expectedOwnerSid) !=
            RecoveryWindowsScratchParentFinding.ObservedButNotTrusted)
            throw new InvalidDataException(
                "Owner SID/DACL объекта недостаточно проверены.");

        return RecoveryNativeHandleIdProbe.ReadFromExistingHandle(handle);
    }

    private static SafeFileHandle OpenNameReadOnly(
        SafeFileHandle parent, string basename)
    {
        nint name = 0, unicodePointer = 0;
        try
        {
            name = Marshal.StringToHGlobalUni(basename);
            var unicode = new UnicodeString
            {
                Length = checked((ushort)(basename.Length * sizeof(char))),
                MaximumLength = checked((ushort)((basename.Length + 1) * sizeof(char))),
                Buffer = name
            };
            unicodePointer = Marshal.AllocHGlobal(
                Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicode, unicodePointer,
                fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = checked((uint)Marshal.SizeOf<ObjectAttributes>()),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodePointer,
                Attributes = ObjCaseInsensitive | ObjDontReparse
            };

            nint raw = 0;
            int status = NtOpenFile(out raw,
                FileReadAttributes | ReadControl | Synchronize,
                ref attributes, out IoStatusBlock ioStatus,
                FileShareAll,
                FileNonDirectoryFile | FileSynchronousIoNonAlert |
                FileOpenReparsePoint);
            SafeFileHandle? opened = raw != 0 && raw != new nint(-1)
                ? new SafeFileHandle(raw, ownsHandle: true) : null;
            if (status != 0 || opened is null || opened.IsInvalid)
            {
                opened?.Dispose();
                RecoveryWindowsScratchNameOpenFinding finding =
                    ClassifyNtOpenStatus(status);
                if (finding == RecoveryWindowsScratchNameOpenFinding.ExclusiveShareDenied)
                    throw new IOException(
                        "STATUS_SHARING_VIOLATION: создатель CC использует ShareAccess=0; " +
                        "это отсутствие свидетельства, а не совпадение имён.");
                throw new IOException(
                    $"NtOpenFile relative read-only отказал: NTSTATUS 0x{status:X8}.");
            }
            return opened;
        }
        finally
        {
            if (unicodePointer != 0) Marshal.FreeHGlobal(unicodePointer);
            if (name != 0) Marshal.FreeHGlobal(name);
        }
    }

    private static void ValidateHandle(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsClosed || handle.IsInvalid)
            throw new ObjectDisposedException(nameof(handle));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        internal ushort Length;
        internal ushort MaximumLength;
        internal nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        internal uint Length;
        internal nint RootDirectory;
        internal nint ObjectName;
        internal uint Attributes;
        internal nint SecurityDescriptor;
        internal nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        internal nint StatusOrPointer;
        internal nuint Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        internal uint Attributes;
        internal uint ReparseTag;
    }

    [DllImport("ntdll.dll", EntryPoint = "NtOpenFile", ExactSpelling = true)]
    private static extern int NtOpenFile(
        out nint fileHandle, uint desiredAccess,
        ref ObjectAttributes attributes, out IoStatusBlock ioStatus,
        uint shareAccess, uint openOptions);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx",
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle, int fileInformationClass,
        out FileAttributeTagInfo info, uint bufferSize);
}

internal enum RecoveryWindowsScratchNameFinding
{
    IncompleteEvidence,
    ParentChanged,
    ReparseOrWrongType,
    CrossVolumeOrAlias,
    NameDoesNotMatchHeld,
    MatchedAtObservation
}

internal enum RecoveryWindowsScratchNameOpenFinding
{
    OpenSucceededUntrusted,
    ExclusiveShareDenied,
    OpenFailed
}

internal readonly record struct RecoveryWindowsScratchNameEvidence(
    RecoveryNativeFileId ParentBefore,
    RecoveryNativeFileId ParentAfter,
    RecoveryNativeFileId HeldBefore,
    RecoveryNativeFileId NamedFirst,
    RecoveryNativeFileId NamedSecond,
    RecoveryNativeFileId HeldAfter,
    bool FirstReparse,
    bool SecondReparse)
{
    internal bool CanRestore => false;
    internal bool CanDelete => false;
    internal bool CanPublish => false;
    internal bool HasTrustedAncestry => false;
}
