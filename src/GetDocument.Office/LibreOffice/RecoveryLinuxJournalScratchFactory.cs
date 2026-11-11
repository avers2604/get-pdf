using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27cb. Linux/Astra LAB-ONLY O_CREAT|O_EXCL creation relative to a
/// CALLER-OWNED held parent directory handle. No path lookup for the child.
/// This is intentionally NOT a trusted factory: parent ancestry, mount/user
/// namespace trust, POSIX ACL/LSM and cross-process writer isolation remain
/// unverified. No production wiring, rename, unlink, path cleanup or commit.
/// On any failure after O_CREAT, the created name is left for manual review.
/// </summary>
internal static class RecoveryLinuxJournalScratchFactory
{
    private const string ScratchSuffix = ".gdjournal.tmp";
    private const long OpenAt2Syscall = 437; // Linux x86_64 and aarch64
    private const ulong OReadWrite = 0x0002;
    private const ulong OCreate = 0x0040;
    private const ulong OExclusive = 0x0080;
    private const ulong ONoFollow = 0x20000;
    private const ulong OCloseOnExec = 0x80000;
    private const ulong ResolveNoXdev = 0x01;
    private const ulong ResolveNoMagicLinks = 0x02;
    private const ulong ResolveNoSymlinks = 0x04;
    private const ulong ResolveBeneath = 0x08;
    private const ulong OwnerReadWrite0600 = 0x0180;
    private const int AtEmptyPath = 0x1000;
    private const int AtSymlinkNoFollow = 0x0100;
    private const uint StatxType = 0x0001;
    private const uint StatxMode = 0x0002;
    private const uint StatxNlink = 0x0004;
    private const uint StatxUid = 0x0008;
    private const uint StatxIno = 0x0100;
    private const uint StatxMntId = 0x1000;

    internal static bool ProductionCreateEnabled => false;
    internal static bool CanAuthorizePublish => false;
    internal static bool CanAuthorizeDelete => false;
    internal static bool CanCleanupByPath => false;

    internal static string ValidateName(string? basename)
    {
        if (basename is null ||
            basename.Length != 32 + ScratchSuffix.Length ||
            !basename.EndsWith(ScratchSuffix, StringComparison.Ordinal))
            throw new ArgumentException(
                "Имя лабораторного журнала должно иметь вид <guidN>.gdjournal.tmp.",
                nameof(basename));
        string nonce = basename[..32];
        if (!nonce.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !Guid.TryParseExact(nonce, "N", out Guid guid) ||
            guid == Guid.Empty)
            throw new ArgumentException(
                "Лабораторный basename содержит недопустимый nonce.",
                nameof(basename));
        return basename;
    }

    /// <summary>
    /// Pure comparison only; it NEVER authorizes a filesystem operation.
    /// </summary>
    internal static RecoveryLinuxJournalScratchFinding Evaluate(
        RecoveryLinuxParentEvidence before,
        RecoveryLinuxParentEvidence after,
        RecoveryLinuxRegularEntryObservation created,
        uint effectiveUid)
    {
        if (!PrivateParent(before, effectiveUid) ||
            !PrivateParent(after, effectiveUid))
            return RecoveryLinuxJournalScratchFinding.IneligibleParent;
        if (before != after)
            return RecoveryLinuxJournalScratchFinding.ParentChanged;
        if (!created.Id.IsComplete ||
            created.Id.Platform != RecoveryNativePlatform.Linux ||
            created.OwnerUid != effectiveUid || created.LinkCount != 1 ||
            created.MountId == 0 ||
            created.Id == before.FileId)
            return RecoveryLinuxJournalScratchFinding.IneligibleNewFile;
        if (created.MountId != before.MountId ||
            created.Id.DeviceOrVolume != before.FileId.DeviceOrVolume)
            return RecoveryLinuxJournalScratchFinding.CrossMountOrDevice;
        return RecoveryLinuxJournalScratchFinding.ObservedButNotAuthorized;
    }

    /// <summary>
    /// A real, lab-only openat2(O_CREAT|O_EXCL) from held parent dirfd.
    /// Parent must have been acquired independently by the laboratory and
    /// examined as a private 0700 directory; this method rechecks but cannot
    /// prove ancestry, namespace, ACL or another user's privileges.
    /// Filename collisions never overwrite or delete the existing entry.
    /// </summary>
    internal static RecoveryLinuxJournalScratchLease CreateExistingParentLab(
        SafeFileHandle heldParent, string basename,
        CancellationToken cancellationToken = default)
    {
        ValidateName(basename);
        ArgumentNullException.ThrowIfNull(heldParent);
        if (heldParent.IsClosed || heldParent.IsInvalid)
            throw new ObjectDisposedException(nameof(heldParent));
        // The environment gate is only an accidental-use safeguard, NOT
        // evidence of authorization or a substitute for trusted ancestry.
        if (!string.Equals(
                Environment.GetEnvironmentVariable("GD31J27CB_NATIVE_LAB"),
                "1", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Создание scratch разрешено только для явно включённой изолированной лаборатории.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() || !BitConverter.IsLittleEndian ||
            RuntimeInformation.ProcessArchitecture is not
                (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException(
                "Адаптер создания scratch поддерживает только Linux x64/arm64.");

        bool parentPinned = false;
        SafeFileHandle? newFile = null;
        FileStream? stream = null;
        try
        {
            heldParent.DangerousAddRef(ref parentPinned);
            uint uid = GetEffectiveUserId();
            RecoveryLinuxParentEvidence before =
                RecoveryPinnedLinuxParentProbe.ReadExistingDirectoryEvidence(
                    heldParent);
            RecoveryPinnedLinuxParentProbe.RequirePrivateOwner(before, uid);
            cancellationToken.ThrowIfCancellationRequested();

            var how = new OpenHow
            {
                Flags = OReadWrite | OCreate | OExclusive | ONoFollow |
                        OCloseOnExec,
                Mode = OwnerReadWrite0600,
                Resolve = ResolveBeneath | ResolveNoSymlinks |
                          ResolveNoMagicLinks | ResolveNoXdev
            };

            long fd;
            try
            {
                fd = SyscallOpenAt2(OpenAt2Syscall,
                    heldParent.DangerousGetHandle().ToInt32(), basename,
                    ref how, (nuint)Marshal.SizeOf<OpenHow>());
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new PlatformNotSupportedException(
                    "libc не предоставила syscall(openat2).", ex);
            }
            if (fd < 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error is 38 or 22)
                    throw new PlatformNotSupportedException(
                        "Ядро или файловая система не подтвердили openat2 create-excl.");
                throw new IOException(
                    "openat2 не создал новый изолированный basename.",
                    new Win32Exception(error));
            }
            // The descriptor is always owned now, even if the next statx,
            // FileStream ctor or cancellation check fails. NEVER unlink it.
            newFile = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
            RecoveryLinuxRegularEntryObservation created =
                ReadPrivateHeldRegular(newFile, uid);
            RecoveryLinuxParentEvidence after =
                RecoveryPinnedLinuxParentProbe.ReadExistingDirectoryEvidence(
                    heldParent);
            if (GetEffectiveUserId() != uid ||
                Evaluate(before, after, created, uid) !=
                    RecoveryLinuxJournalScratchFinding.ObservedButNotAuthorized)
                throw new InvalidDataException(
                    "Scratch file или parent fd изменился после O_EXCL.");

            stream = new FileStream(newFile, FileAccess.ReadWrite,
                bufferSize: 4096, isAsync: true);
            newFile = null; // FileStream owns handle from this point
            if (stream.Length != 0)
                throw new InvalidDataException(
                    "Новый scratch file неожиданно содержит данные.");
            cancellationToken.ThrowIfCancellationRequested();

            var result = new RecoveryLinuxJournalScratchLease(
                heldParent, stream, basename, before, created, uid);
            stream = null;
            parentPinned = false; // release ownership transferred to lease
            return result;
        }
        finally
        {
            stream?.Dispose();
            newFile?.Dispose();
            if (parentPinned) heldParent.DangerousRelease();
        }
    }

    internal static RecoveryLinuxRegularEntryObservation ReadPrivateHeldRegular(
        SafeFileHandle heldFile, uint effectiveUid)
    {
        ArgumentNullException.ThrowIfNull(heldFile);
        if (heldFile.IsClosed || heldFile.IsInvalid)
            throw new ObjectDisposedException(nameof(heldFile));
        bool pinned = false;
        try
        {
            heldFile.DangerousAddRef(ref pinned);
            byte[] stat = new byte[256];
            int status = Statx(heldFile.DangerousGetHandle().ToInt32(), "",
                AtEmptyPath | AtSymlinkNoFollow,
                StatxType | StatxMode | StatxNlink | StatxUid |
                StatxIno | StatxMntId, stat);
            if (status != 0)
                throw new IOException("statx не подтвердил приватный scratch fd.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            return RecoveryLinuxNamedPairProbe.ParseRegularStatx(stat,
                effectiveUid);
        }
        finally
        {
            if (pinned) heldFile.DangerousRelease();
        }
    }

    private static bool PrivateParent(
        RecoveryLinuxParentEvidence parent, uint euid)
    {
        try
        {
            RecoveryPinnedLinuxParentProbe.RequirePrivateOwner(parent, euid);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
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

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string emptyPath,
        int flags, uint mask, [Out] byte[] output);

    [DllImport("libc", EntryPoint = "geteuid", ExactSpelling = true)]
    private static extern uint GetEffectiveUserId();
}

internal enum RecoveryLinuxJournalScratchFinding
{
    IneligibleParent,
    ParentChanged,
    IneligibleNewFile,
    CrossMountOrDevice,
    ObservedButNotAuthorized
}

/// <summary>
/// Owns only the just-created file stream and one DangerousAddRef on the
/// CALLER-OWNED parent. Dispose closes handles but intentionally DOES NOT
/// remove the directory entry, even when there is a fault or cancellation.
/// </summary>
internal sealed class RecoveryLinuxJournalScratchLease : IDisposable
{
    private SafeFileHandle? _parent;
    private FileStream? _stream;
    private readonly uint _uid;
    private readonly RecoveryLinuxParentEvidence _parentAtCreate;

    internal string Basename { get; }
    internal RecoveryLinuxRegularEntryObservation CreatedIdentity { get; }
    internal bool CanAuthorizePublish => false;
    internal bool CanAuthorizeDelete => false;
    internal bool CanCleanupByPath => false;
    internal bool HasTrustedParentAncestry => false;
    internal bool HasVerifiedDurableDirectoryEntry => false;

    internal FileStream Stream =>
        _stream ?? throw new ObjectDisposedException(
            nameof(RecoveryLinuxJournalScratchLease));

    internal RecoveryLinuxJournalScratchLease(
        SafeFileHandle parent, FileStream stream, string basename,
        RecoveryLinuxParentEvidence evidence,
        RecoveryLinuxRegularEntryObservation created, uint uid)
    {
        _parent = parent;
        _stream = stream;
        Basename = basename;
        _parentAtCreate = evidence;
        CreatedIdentity = created;
        _uid = uid;
    }

    internal RecoveryLinuxJournalScratchFinding InspectExistingHandles(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SafeFileHandle parent = _parent ?? throw new ObjectDisposedException(
            nameof(RecoveryLinuxJournalScratchLease));
        FileStream stream = Stream;
        RecoveryLinuxParentEvidence after =
            RecoveryPinnedLinuxParentProbe.ReadExistingDirectoryEvidence(parent);
        var file = RecoveryLinuxJournalScratchFactory.ReadPrivateHeldRegular(
            stream.SafeFileHandle, _uid);
        if (file != CreatedIdentity)
            return RecoveryLinuxJournalScratchFinding.IneligibleNewFile;
        return RecoveryLinuxJournalScratchFactory.Evaluate(
            _parentAtCreate, after, file, _uid);
    }

    public void Dispose()
    {
        FileStream? stream = Interlocked.Exchange(ref _stream, null);
        SafeFileHandle? parent = Interlocked.Exchange(ref _parent, null);
        try { stream?.Dispose(); }
        finally { parent?.DangerousRelease(); }
    }
}

/// <summary>
/// No Windows pathname fallback. Implement NT CreateFile relative to a trusted
/// ROOT_DIRECTORY handle only after independent ACL/token/reparse auditing.
/// </summary>
internal static class RecoveryWindowsJournalScratchGate
{
    internal static bool NativeRelativeCreateReady => false;
    internal static bool CanCreateByPath => false;
    internal static bool CanAuthorizeProductionCreate => false;
}
