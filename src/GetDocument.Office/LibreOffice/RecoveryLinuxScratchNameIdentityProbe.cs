using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27cd: READ-ONLY Linux scratch basename-to-held-FD observation.
/// Two independent openat2(O_PATH|O_NOFOLLOW) calls expose some concurrent
/// replacements but do NOT freeze a directory entry after the final check.
/// This does not confer any authority to commit, restore, delete or cleanup.
/// No production wiring to OfficeRecoveryStore.
/// </summary>
internal static class RecoveryLinuxScratchNameIdentityProbe
{
    private const long OpenAt2Syscall = 437; // Linux x64, arm64
    private const ulong OPath = 0x200000;
    private const ulong ONoFollow = 0x20000;
    private const ulong OCloexec = 0x80000;
    private const ulong ResolveNoXdev = 0x01;
    private const ulong ResolveNoMagicLinks = 0x02;
    private const ulong ResolveNoSymlinks = 0x04;
    private const ulong ResolveBeneath = 0x08;

    internal static bool ProductionNameAuthorityEnabled => false;
    internal static bool CanAuthorizeCleanup => false;
    internal static bool CanAuthorizePublication => false;

    internal static RecoveryScratchNameFinding Evaluate(
        RecoveryLinuxScratchNameEvidence evidence)
    {
        RecoveryLinuxParentEvidence parent = evidence.ParentBefore;
        if (parent.FileId.Platform != RecoveryNativePlatform.Linux ||
            !parent.FileId.IsComplete || parent.MountId == 0 ||
            parent.OwnerUid != evidence.ParentAfter.OwnerUid ||
            (parent.Mode & 0xF000) != 0x4000 ||
            (parent.Mode & 0x0FFF) != 0x01C0)
            return RecoveryScratchNameFinding.IncompleteEvidence;

        if (parent != evidence.ParentAfter)
            return RecoveryScratchNameFinding.ParentChanged;

        var files = new[]
        {
            evidence.HeldBefore, evidence.NamedFirst,
            evidence.NamedSecond, evidence.HeldAfter
        };
        if (files.Any(x =>
                !x.Id.IsComplete || x.Id.Platform != RecoveryNativePlatform.Linux ||
                x.MountId == 0 || x.LinkCount != 1))
            return RecoveryScratchNameFinding.IncompleteEvidence;

        if (files.Any(x =>
                x.MountId != parent.MountId ||
                x.Id.DeviceOrVolume != parent.FileId.DeviceOrVolume ||
                x.OwnerUid != parent.OwnerUid))
            return RecoveryScratchNameFinding.CrossVolumeOrMount;

        if (evidence.HeldBefore.Id == parent.FileId ||
            files.Any(x => x != evidence.HeldBefore))
            return RecoveryScratchNameFinding.NameDoesNotMatchHeld;

        return RecoveryScratchNameFinding.MatchedAtObservation;
    }

    /// <summary>
    /// The caller MUST own both parent and created file handles, which are
    /// pinned while the read-only observation runs. Named child handles are
    /// opened from parent fd, never from a path. No user documents accessed.
    /// </summary>
    internal static RecoveryLinuxScratchNameEvidence ObserveExisting(
        SafeFileHandle heldParent, SafeFileHandle heldFile, string basename,
        CancellationToken cancellationToken = default)
    {
        RecoveryLinuxJournalScratchFactory.ValidateName(basename);
        ValidateHandle(heldParent);
        ValidateHandle(heldFile);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() || !BitConverter.IsLittleEndian ||
            RuntimeInformation.ProcessArchitecture is not
                (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException(
                "Linux scratch-name probe работает только на Linux x64/arm64.");

        bool parentPinned = false, filePinned = false;
        try
        {
            heldParent.DangerousAddRef(ref parentPinned);
            heldFile.DangerousAddRef(ref filePinned);
            uint uid = GetEffectiveUserId();

            var before =
                RecoveryPinnedLinuxParentProbe.ReadExistingDirectoryEvidence(heldParent);
            RecoveryPinnedLinuxParentProbe.RequirePrivateOwner(before, uid);
            var heldBefore = RecoveryLinuxJournalScratchFactory.ReadPrivateHeldRegular(
                heldFile, uid);
            cancellationToken.ThrowIfCancellationRequested();

            using SafeFileHandle firstName = OpenRelativeReadOnly(heldParent, basename);
            var namedFirst = RecoveryLinuxJournalScratchFactory.ReadPrivateHeldRegular(
                firstName, uid);
            cancellationToken.ThrowIfCancellationRequested();

            using SafeFileHandle secondName = OpenRelativeReadOnly(heldParent, basename);
            var namedSecond = RecoveryLinuxJournalScratchFactory.ReadPrivateHeldRegular(
                secondName, uid);
            var heldAfter = RecoveryLinuxJournalScratchFactory.ReadPrivateHeldRegular(
                heldFile, uid);
            var after =
                RecoveryPinnedLinuxParentProbe.ReadExistingDirectoryEvidence(heldParent);
            RecoveryPinnedLinuxParentProbe.RequirePrivateOwner(after, uid);
            if (GetEffectiveUserId() != uid)
                throw new InvalidDataException(
                    "Во время наблюдения имени сменился эффективный UID.");

            var record = new RecoveryLinuxScratchNameEvidence(
                before, after, heldBefore, namedFirst, namedSecond, heldAfter);
            if (Evaluate(record) != RecoveryScratchNameFinding.MatchedAtObservation)
                throw new InvalidDataException(
                    "Имя больше не указывает на тот же удерживаемый scratch inode.");
            cancellationToken.ThrowIfCancellationRequested();
            return record;
        }
        finally
        {
            if (filePinned) heldFile.DangerousRelease();
            if (parentPinned) heldParent.DangerousRelease();
        }
    }

    private static SafeFileHandle OpenRelativeReadOnly(
        SafeFileHandle parent, string basename)
    {
        var how = new OpenHow
        {
            Flags = OPath | ONoFollow | OCloexec,
            Mode = 0,
            Resolve = ResolveBeneath | ResolveNoSymlinks |
                      ResolveNoMagicLinks | ResolveNoXdev
        };
        long raw = SyscallOpenAt2(OpenAt2Syscall,
            parent.DangerousGetHandle().ToInt32(),
            basename, ref how, (nuint)Marshal.SizeOf<OpenHow>());
        if (raw < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            if (errno is 38 or 22) // ENOSYS, EINVAL
                throw new PlatformNotSupportedException(
                    "Ядро не подтверждает openat2 в текущем mount namespace.");
            throw new IOException(
                "Не удалось повторно открыть имя только для наблюдения.",
                new Win32Exception(errno));
        }
        return new SafeFileHandle((IntPtr)raw, ownsHandle: true);
    }

    private static void ValidateHandle(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid || handle.IsClosed)
            throw new ObjectDisposedException(nameof(handle));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow
    {
        internal ulong Flags;
        internal ulong Mode;
        internal ulong Resolve;
    }

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long SyscallOpenAt2(
        long syscallNumber, int dirfd,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string basename,
        ref OpenHow how, nuint size);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}

internal enum RecoveryScratchNameFinding
{
    IncompleteEvidence,
    ParentChanged,
    CrossVolumeOrMount,
    NameDoesNotMatchHeld,
    MatchedAtObservation
}

internal readonly record struct RecoveryLinuxScratchNameEvidence(
    RecoveryLinuxParentEvidence ParentBefore,
    RecoveryLinuxParentEvidence ParentAfter,
    RecoveryLinuxRegularEntryObservation HeldBefore,
    RecoveryLinuxRegularEntryObservation NamedFirst,
    RecoveryLinuxRegularEntryObservation NamedSecond,
    RecoveryLinuxRegularEntryObservation HeldAfter)
{
    internal bool CanRestore => false;
    internal bool CanDelete => false;
    internal bool CanPublish => false;
    internal bool HasTrustedAncestry => false;
}
