using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27cc: WINDOWS ONLY, EXPLICIT-OPT-IN LABORATORY NtCreateFile
/// FILE_CREATE with OBJECT_ATTRIBUTES.RootDirectory = caller-held directory.
/// Not a trusted file factory: ancestry, effective DACL, privileges, MIC,
/// cross-host SMB locks, reparse handling and directory durability are NOT
/// independently attested. On any failure AFTER creation the name is left
/// for investigation. Never rename, unlink or mutate user documents.
/// DO NOT WIRE into production OfficeRecoveryStore.
/// </summary>
internal static class RecoveryWindowsJournalScratchFactory
{
    private const string ScratchSuffix = ".gdjournal.tmp";
    private const uint FileReadData = 0x00000001;
    private const uint FileWriteData = 0x00000002;
    private const uint FileReadAttributes = 0x00000080;
    private const uint ReadControl = 0x00020000;
    private const uint Synchronize = 0x00100000;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileCreate = 0x00000002; // create-new only
    private const uint FileCreated = 0x00000002;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint ObjCaseInsensitive = 0x00000040;
    private const uint ObjDontReparse = 0x00001000;
    private const int FileAttributeTagInfoClass = 9;

    internal static bool ProductionCreateEnabled => false;
    internal static bool CanCreateByPath => false;
    internal static bool CanAuthorizeCleanup => false;
    internal static bool CanAuthorizePublication => false;
    internal static bool HasVerifiedEffectiveAccess => false;
    internal static bool HasTrustedAncestry => false;
    internal static bool HasVerifiedReparseIsolation => false;
    internal static bool HasVerifiedDirectoryDurability => false;

    internal static string ValidateName(string? basename)
    {
        if (basename is null ||
            basename.Length != 32 + ScratchSuffix.Length ||
            !basename.EndsWith(ScratchSuffix, StringComparison.Ordinal))
            throw new ArgumentException(
                "Допустимо только имя <guidN-lowercase>.gdjournal.tmp.",
                nameof(basename));
        string nonce = basename[..32];
        if (!nonce.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !Guid.TryParseExact(nonce, "N", out Guid parsed) || parsed == Guid.Empty)
            throw new ArgumentException("Имя scratch не является уникальным GUID.",
                nameof(basename));
        return basename;
    }

    /// <summary>
    /// Diagnostic negative screening only: it deliberately cannot prove
    /// effective Windows access, token privileges, inherited ACLs or integrity.
    /// A "matched" result is still not a file-creation capability.
    /// </summary>
    internal static RecoveryWindowsScratchParentFinding EvaluateParent(
        RecoveryWindowsParentEvidence parent,
        RecoveryWindowsAclRiskEvidence acl,
        string expectedOwnerSid)
    {
        if (parent.FileId.Platform != RecoveryNativePlatform.Windows ||
            !parent.FileId.IsComplete)
            return RecoveryWindowsScratchParentFinding.Incomplete;
        if (RecoveryWindowsParentSecurityGate.Evaluate(parent, expectedOwnerSid)
            != RecoveryWindowsParentDecision.RequiresExplicitAceAudit)
            return RecoveryWindowsScratchParentFinding.InvalidOwnerOrDacl;
        if (acl.Aces is null || acl.AceCount != acl.Aces.Count ||
            acl.BroadWriteAllowCount != 0 || acl.UnreviewedAceCount != 0 ||
            acl.DenyAceCount != 0)
            return RecoveryWindowsScratchParentFinding.UnreviewedAcl;
        return RecoveryWindowsScratchParentFinding.ObservedButNotTrusted;
    }

    internal static RecoveryWindowsScratchFileFinding EvaluateFileIdentity(
        RecoveryNativeFileId parentAtOpen,
        RecoveryNativeFileId parentAtRecheck,
        RecoveryNativeFileId newFile)
    {
        if (!parentAtOpen.IsComplete || !parentAtRecheck.IsComplete ||
            !newFile.IsComplete ||
            parentAtOpen.Platform != RecoveryNativePlatform.Windows ||
            parentAtRecheck.Platform != RecoveryNativePlatform.Windows ||
            newFile.Platform != RecoveryNativePlatform.Windows)
            return RecoveryWindowsScratchFileFinding.Incomplete;
        if (parentAtOpen != parentAtRecheck)
            return RecoveryWindowsScratchFileFinding.ParentChanged;
        if (newFile == parentAtOpen ||
            newFile.DeviceOrVolume != parentAtOpen.DeviceOrVolume)
            return RecoveryWindowsScratchFileFinding.CrossVolumeOrAlias;
        return RecoveryWindowsScratchFileFinding.ObservedButNotTrusted;
    }

    internal static bool SameAclSnapshot(
        RecoveryWindowsAclRiskEvidence before,
        RecoveryWindowsAclRiskEvidence after) =>
        before.Aces is not null && after.Aces is not null &&
        before.AceCount == after.AceCount &&
        before.AceCount == before.Aces.Count &&
        after.AceCount == after.Aces.Count &&
        before.BroadWriteAllowCount == after.BroadWriteAllowCount &&
        before.UnreviewedAceCount == after.UnreviewedAceCount &&
        before.DenyAceCount == after.DenyAceCount &&
        before.Aces.SequenceEqual(after.Aces);

    internal static RecoveryWindowsScratchTagFinding EvaluateTag(
        uint attributes, bool wantDirectory)
    {
        if (attributes == 0 || (attributes & FileAttributeReparsePoint) != 0 ||
            ((attributes & FileAttributeDirectory) != 0) != wantDirectory)
            return RecoveryWindowsScratchTagFinding.ReparseOrWrongType;
        return RecoveryWindowsScratchTagFinding.NormalTypeButUntrusted;
    }

    /// <summary>
    /// Actual NT create operation is gated by GD31J27CC_NATIVE_LAB=1.
    /// The operator must separately provide an ACL-qualified PRIVATE
    /// test directory opened with directory and READ_CONTROL access.
    /// No paths passed to NtCreateFile; no automatic deletion on any error.
    /// </summary>
    internal static RecoveryWindowsJournalScratchLease CreateOnHeldParentForLab(
        SafeFileHandle heldParent, string basename, string expectedOwnerSid,
        CancellationToken cancellationToken = default)
    {
        ValidateName(basename);
        ArgumentNullException.ThrowIfNull(heldParent);
        if (heldParent.IsInvalid || heldParent.IsClosed)
            throw new ObjectDisposedException(nameof(heldParent));
        if (!string.Equals(
                Environment.GetEnvironmentVariable("GD31J27CC_NATIVE_LAB"),
                "1", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Нативный Windows create разрешён только в явно включённой лаборатории.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() ||
            RuntimeInformation.ProcessArchitecture is not
                (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException(
                "NT relative create квалифицируется только для Windows x64/arm64.");

        bool parentPinned = false;
        SafeFileHandle? newlyCreated = null;
        FileStream? stream = null;
        nint unmanagedName = 0;
        nint unicodePointer = 0;
        try
        {
            heldParent.DangerousAddRef(ref parentPinned);
            RequireNormalDirectory(heldParent);
            RecoveryWindowsParentEvidence before =
                RecoveryWindowsParentSecurityProbe.ObserveExistingHandle(heldParent);
            RecoveryWindowsAclRiskEvidence aclBefore =
                RecoveryWindowsAclRiskObserver.ObserveExistingHandle(heldParent);
            if (EvaluateParent(before, aclBefore, expectedOwnerSid) !=
                RecoveryWindowsScratchParentFinding.ObservedButNotTrusted)
                throw new InvalidDataException(
                    "Родительская DACL/owner-диагностика не прошла отрицательные фильтры.");
            cancellationToken.ThrowIfCancellationRequested();

            // Explicit unmanaged UNICODE_STRING length, not arbitrary
            // .NET Path methods or a path-based CreateFileW fallback.
            unmanagedName = Marshal.StringToHGlobalUni(basename);
            var unicode = new UnicodeString
            {
                Length = checked((ushort)(basename.Length * sizeof(char))),
                MaximumLength = checked((ushort)((basename.Length + 1) * sizeof(char))),
                Buffer = unmanagedName
            };
            unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicode, unicodePointer, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = checked((uint)Marshal.SizeOf<ObjectAttributes>()),
                RootDirectory = heldParent.DangerousGetHandle(),
                ObjectName = unicodePointer,
                Attributes = ObjCaseInsensitive | ObjDontReparse,
                SecurityDescriptor = 0,
                SecurityQualityOfService = 0
            };

            nint raw = 0;
            int status = NtCreateFile(out raw,
                FileReadData | FileWriteData | FileReadAttributes |
                ReadControl | Synchronize,
                ref attributes, out IoStatusBlock ioStatus,
                0, FileAttributeNormal,
                0, // deny concurrent opens while this lab handle is held
                FileCreate,
                FileSynchronousIoNonAlert | FileNonDirectoryFile |
                FileOpenReparsePoint,
                0, 0);
            // Wrap any handle returned by the OS BEFORE checking status.
            // No leak even if the OS reports an unexpected IoStatusBlock.
            if (raw != 0 && raw != new nint(-1))
                newlyCreated = new SafeFileHandle(raw, ownsHandle: true);
            if (status != 0 || newlyCreated is null ||
                newlyCreated.IsInvalid || ioStatus.Information != (nuint)FileCreated)
                throw new IOException(
                    $"NtCreateFile(FILE_CREATE) отказал: NTSTATUS 0x{status:X8}; " +
                    "созданный объект (если существует) не удаляется.");

            RequireNormalRegularFile(newlyCreated);
            RecoveryNativeFileId childId =
                RecoveryNativeHandleIdProbe.ReadFromExistingHandle(newlyCreated);
            RecoveryWindowsParentEvidence after =
                RecoveryWindowsParentSecurityProbe.ObserveExistingHandle(heldParent);
            RecoveryWindowsAclRiskEvidence aclAfter =
                RecoveryWindowsAclRiskObserver.ObserveExistingHandle(heldParent);
            RecoveryWindowsParentEvidence childSecurity =
                RecoveryWindowsParentSecurityProbe.ObserveExistingHandle(newlyCreated);
            RecoveryWindowsAclRiskEvidence childAcl =
                RecoveryWindowsAclRiskObserver.ObserveExistingHandle(newlyCreated);
            RequireNormalDirectory(heldParent);

            if (!SameAclSnapshot(aclBefore, aclAfter) ||
                childSecurity.FileId != childId ||
                EvaluateParent(after, aclAfter, expectedOwnerSid) !=
                    RecoveryWindowsScratchParentFinding.ObservedButNotTrusted ||
                EvaluateParent(childSecurity, childAcl, expectedOwnerSid) !=
                    RecoveryWindowsScratchParentFinding.ObservedButNotTrusted ||
                EvaluateFileIdentity(before.FileId, after.FileId, childId) !=
                    RecoveryWindowsScratchFileFinding.ObservedButNotTrusted)
                throw new InvalidDataException(
                    "После NT create обнаружено изменение parent, File ID или ACL.");

            stream = new FileStream(newlyCreated, FileAccess.ReadWrite,
                bufferSize: 4096, isAsync: false);
            newlyCreated = null; // FileStream owns new file handle
            if (stream.Length != 0)
                throw new InvalidDataException("Новый scratch неожиданно не пуст.");
            cancellationToken.ThrowIfCancellationRequested();

            var lease = new RecoveryWindowsJournalScratchLease(
                heldParent, stream, basename, before, childId,
                expectedOwnerSid);
            stream = null;
            parentPinned = false; // DangerousRelease transferred to lease
            return lease;
        }
        finally
        {
            stream?.Dispose();
            newlyCreated?.Dispose();
            if (unicodePointer != 0) Marshal.FreeHGlobal(unicodePointer);
            if (unmanagedName != 0) Marshal.FreeHGlobal(unmanagedName);
            if (parentPinned) heldParent.DangerousRelease();
        }
    }

    private static void RequireNormalDirectory(SafeFileHandle handle) =>
        RequireTag(handle, wantDirectory: true);

    private static void RequireNormalRegularFile(SafeFileHandle handle) =>
        RequireTag(handle, wantDirectory: false);

    private static void RequireTag(SafeFileHandle handle, bool wantDirectory)
    {
        if (!GetFileInformationByHandleEx(handle,
                FileAttributeTagInfoClass, out FileAttributeTagInfo data, 8))
            throw new IOException("Windows не вернула FILE_ATTRIBUTE_TAG_INFO.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        if (EvaluateTag(data.Attributes, wantDirectory) !=
            RecoveryWindowsScratchTagFinding.NormalTypeButUntrusted)
            throw new InvalidDataException(
                "Новый file/parent fd не соответствует обычному типу без reparse.");
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

    [DllImport("ntdll.dll", EntryPoint = "NtCreateFile", ExactSpelling = true)]
    private static extern int NtCreateFile(
        out nint fileHandle, uint desiredAccess,
        ref ObjectAttributes attributes, out IoStatusBlock ioStatus,
        nint allocationSize, uint fileAttributes,
        uint shareAccess, uint createDisposition,
        uint createOptions, nint eaBuffer, uint eaLength);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx",
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle, int fileInformationClass,
        out FileAttributeTagInfo info, uint bufferSize);
}

internal enum RecoveryWindowsScratchTagFinding
{
    ReparseOrWrongType,
    NormalTypeButUntrusted
}

internal enum RecoveryWindowsScratchParentFinding
{
    Incomplete,
    InvalidOwnerOrDacl,
    UnreviewedAcl,
    ObservedButNotTrusted
}

internal enum RecoveryWindowsScratchFileFinding
{
    Incomplete,
    ParentChanged,
    CrossVolumeOrAlias,
    ObservedButNotTrusted
}

/// <summary>
/// DangerousAddRef on the caller-owned parent plus owned new FileStream.
/// Closing the lease NEVER deletes or changes the directory entry.
/// Caller must keep its original SafeFileHandle wrapper open until Dispose.
/// </summary>
internal sealed class RecoveryWindowsJournalScratchLease : IDisposable
{
    private SafeFileHandle? _parent;
    private FileStream? _stream;
    internal string Basename { get; }
    internal RecoveryNativeFileId ParentId { get; }
    internal RecoveryNativeFileId CreatedId { get; }
    internal string ExpectedOwnerSid { get; }

    internal RecoveryWindowsJournalScratchLease(
        SafeFileHandle parent, FileStream stream, string basename,
        RecoveryWindowsParentEvidence parentEvidence,
        RecoveryNativeFileId createdId, string owner)
    {
        _parent = parent;
        _stream = stream;
        Basename = basename;
        ParentId = parentEvidence.FileId;
        CreatedId = createdId;
        ExpectedOwnerSid = owner;
    }

    internal FileStream Stream => _stream ??
        throw new ObjectDisposedException(nameof(RecoveryWindowsJournalScratchLease));

    internal RecoveryWindowsScratchFileFinding InspectHeldIdentifiers()
    {
        SafeFileHandle parent = _parent ??
            throw new ObjectDisposedException(nameof(RecoveryWindowsJournalScratchLease));
        FileStream child = Stream;
        var parentNow = RecoveryNativeHandleIdProbe.ReadFromExistingHandle(parent);
        var childNow = RecoveryNativeHandleIdProbe.ReadFromExistingHandle(
            child.SafeFileHandle);
        if (childNow != CreatedId)
            return RecoveryWindowsScratchFileFinding.ParentChanged;
        return RecoveryWindowsJournalScratchFactory.EvaluateFileIdentity(
            ParentId, parentNow, childNow);
    }

    internal bool CanAuthorizeRecovery => false;
    internal bool CanAuthorizeDelete => false;
    internal bool CanAuthorizePublish => false;
    internal bool HasIndependentOwnerVerification => false;

    public void Dispose()
    {
        FileStream? child = Interlocked.Exchange(ref _stream, null);
        SafeFileHandle? parent = Interlocked.Exchange(ref _parent, null);
        try { child?.Dispose(); }
        finally { parent?.DangerousRelease(); }
    }
}

internal readonly record struct RecoveryWindowsScratchObservation(
    RecoveryNativeFileId Parent,
    RecoveryNativeFileId Child,
    string Basename,
    string ExpectedOwnerSid)
{
    internal bool CanAuthorizeRecovery => false;
    internal bool CanAuthorizeDelete => false;
    internal bool CanAuthorizePublish => false;
    internal bool HasIndependentOwnerVerification => false;
}
