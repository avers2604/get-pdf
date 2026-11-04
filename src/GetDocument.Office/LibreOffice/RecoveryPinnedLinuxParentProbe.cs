using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27bq: Linux-only READ-ONLY relative open from an ALREADY OPEN
/// anchor directory handle. The caller remains responsible for proving
/// how that anchor was obtained and whether its ancestors are trustworthy.
/// No path-based recovery root is promoted to a trusted sandbox by this probe.
/// Not connected to production CaptureAsync or native delete.
/// </summary>
internal static class RecoveryPinnedLinuxParentProbe
{
    private const long OpenAt2Syscall = 437; // x86_64 / aarch64 Linux UAPI
    private const ulong OPath = 0x200000;
    private const ulong ODirectory = 0x10000;
    private const ulong ONoFollow = 0x20000;
    private const ulong OCloexec = 0x80000;
    private const ulong ResolveNoXdev = 0x01;
    private const ulong ResolveNoMagicLinks = 0x02;
    private const ulong ResolveNoSymlinks = 0x04;
    private const ulong ResolveBeneath = 0x08;
    private const int AtEmptyPath = 0x1000;
    private const int AtSymlinkNoFollow = 0x0100;
    private const uint StatxType = 0x0001;
    private const uint StatxMode = 0x0002;
    private const uint StatxUid = 0x0008;
    private const uint StatxIno = 0x0100;
    private const uint StatxMntId = 0x1000;
    private const int StatxBytes = 256;
    private const ushort DirectoryType = 0x4000;
    private const ushort Owner0700 = 0x01C0;

    internal static void ValidateChildName(string childName)
    {
        if (childName == "recovery-volume-coordinator") return;
        if (childName is { Length: 64 } &&
            childName.All(c => c is >= '0' and <= '9' or
                                 >= 'A' and <= 'F' or
                                 >= 'a' and <= 'f'))
            return;
        throw new ArgumentException(
            "Имя дочернего каталога не соответствует согласованной грамматике.",
            nameof(childName));
    }

    internal static RecoveryLinuxParentEvidence ParseDirectoryStatx(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length != StatxBytes)
            throw new ArgumentException("Требуется statx размером 256 байт.",
                nameof(bytes));
        var b = bytes.AsSpan();
        const uint required = StatxType | StatxMode | StatxUid |
                              StatxIno | StatxMntId;
        uint mask = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(0, 4));
        if ((mask & required) != required)
            throw new InvalidDataException(
                "Нет полного набора Linux statx: type/mode/uid/inode/mount.");

        ushort mode = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(28, 2));
        if ((mode & 0xF000) != DirectoryType)
            throw new InvalidDataException(
                "Открытый дескриптор не является каталогом.");

        uint uid = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(20, 4));
        ulong mountId = BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(144, 8));
        if (mountId == 0)
            throw new InvalidDataException("Отсутствует Linux statx mount ID.");

        RecoveryNativeFileId fileId = RecoveryNativeHandleIdProbe.ParseLinuxStatx(bytes);
        return new RecoveryLinuxParentEvidence(fileId, uid, mode, mountId);
    }

    internal static void RequirePrivateOwner(
        RecoveryLinuxParentEvidence evidence, uint effectiveUid)
    {
        if (evidence.FileId.Platform != RecoveryNativePlatform.Linux ||
            !evidence.FileId.IsComplete ||
            evidence.OwnerUid != effectiveUid ||
            evidence.MountId == 0 ||
            (evidence.Mode & 0xF000) != DirectoryType ||
            (evidence.Mode & 0x0FFF) != Owner0700)
            throw new InvalidDataException(
                "Каталог не подтверждён как приватный 0700, принадлежащий текущему UID.");
    }

    internal static RecoveryPinnedLinuxParentLease OpenChildRelative(
        SafeFileHandle heldAnchor, string childName)
    {
        ArgumentNullException.ThrowIfNull(heldAnchor);
        if (heldAnchor.IsClosed || heldAnchor.IsInvalid)
            throw new ObjectDisposedException(nameof(heldAnchor));
        ValidateChildName(childName);

        if (!OperatingSystem.IsLinux() || !BitConverter.IsLittleEndian ||
            RuntimeInformation.ProcessArchitecture is not
                (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException(
                "Linux openat2 из directory fd не подтверждён для данной ОС/архитектуры.");

        bool pinned = false;
        SafeFileHandle? child = null;
        try
        {
            // Retain the caller's anchor handle throughout the returned
            // observation lease: closing its original wrapper must not
            // recycle its numeric fd during relative open/inspection.
            heldAnchor.DangerousAddRef(ref pinned);
            RecoveryLinuxParentEvidence first = ReadHeldDirectoryEvidence(heldAnchor);
            uint euid = GetEffectiveUserId();
            RequirePrivateOwner(first, euid);

            var how = new OpenHow
            {
                Flags = OPath | ODirectory | ONoFollow | OCloexec,
                Mode = 0,
                Resolve = ResolveBeneath | ResolveNoSymlinks |
                          ResolveNoMagicLinks | ResolveNoXdev
            };
            long fd;
            try
            {
                fd = SyscallOpenAt2(OpenAt2Syscall,
                    heldAnchor.DangerousGetHandle().ToInt32(),
                    childName, ref how, (nuint)Marshal.SizeOf<OpenHow>());
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new PlatformNotSupportedException(
                    "libc syscall(openat2) недоступен.", ex);
            }
            if (fd < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno is 38 or 22) // ENOSYS / unsupported kernel open_how
                    throw new PlatformNotSupportedException(
                        "Ядро Astra/Linux не подтвердило openat2(RESOLVE_BENEATH/NO_XDEV).");
                throw new IOException(
                    "Не удалось открыть каталог относительно закреплённого anchor fd.",
                    new Win32Exception(errno));
            }

            child = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
            RecoveryLinuxParentEvidence opened = ReadHeldDirectoryEvidence(child);
            RequirePrivateOwner(opened, euid);
            RecoveryLinuxParentEvidence rechecked = ReadHeldDirectoryEvidence(heldAnchor);
            RequirePrivateOwner(rechecked, euid);
            if (first != rechecked || first.MountId != opened.MountId ||
                first.FileId.DeviceOrVolume != opened.FileId.DeviceOrVolume)
                throw new InvalidDataException(
                    "Доверенный родитель или точка монтирования изменились при relative open.");

            var lease = new RecoveryPinnedLinuxParentLease(
                heldAnchor, child, first, rechecked, opened);
            child = null; // transferred to the lease
            pinned = false; // DangerousRelease also transferred to lease
            return lease;
        }
        finally
        {
            child?.Dispose();
            if (pinned) heldAnchor.DangerousRelease();
        }
    }

    private static RecoveryLinuxParentEvidence ReadHeldDirectoryEvidence(
        SafeFileHandle handle)
    {
        bool pinned = false;
        try
        {
            handle.DangerousAddRef(ref pinned);
            byte[] data = new byte[StatxBytes];
            int result = Statx(handle.DangerousGetHandle().ToInt32(), "",
                AtEmptyPath | AtSymlinkNoFollow,
                StatxType | StatxMode | StatxUid | StatxIno | StatxMntId, data);
            if (result != 0)
                throw new IOException("Не удалось проверить statx directory handle.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            return ParseDirectoryStatx(data);
        }
        finally
        {
            if (pinned) handle.DangerousRelease();
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
        long number, int directoryFd,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string relativeName,
        ref OpenHow how, nuint size);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int flags, uint mask, [Out] byte[] data);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}

internal readonly record struct RecoveryLinuxParentEvidence(
    RecoveryNativeFileId FileId, uint OwnerUid, ushort Mode, ulong MountId);

/// <summary>
/// Holds an already supplied anchor fd reference and a child opened with
/// openat2. This is diagnostic evidence, never a delete/write capability.
/// The anchor itself is NOT proven rooted at a trusted filesystem boundary.
/// </summary>
internal sealed class RecoveryPinnedLinuxParentLease : IDisposable
{
    private SafeFileHandle? _anchor;
    private SafeFileHandle? _child;
    internal RecoveryLinuxParentEvidence ParentAtOpen { get; }
    internal RecoveryLinuxParentEvidence ParentAtRecheck { get; }
    internal RecoveryLinuxParentEvidence Child { get; }

    internal RecoveryPinnedLinuxParentLease(
        SafeFileHandle anchor, SafeFileHandle child,
        RecoveryLinuxParentEvidence before,
        RecoveryLinuxParentEvidence after,
        RecoveryLinuxParentEvidence childEvidence)
    {
        _anchor = anchor;
        _child = child;
        ParentAtOpen = before;
        ParentAtRecheck = after;
        Child = childEvidence;
    }

    public void Dispose()
    {
        SafeFileHandle? child = Interlocked.Exchange(ref _child, null);
        SafeFileHandle? anchor = Interlocked.Exchange(ref _anchor, null);
        try { child?.Dispose(); }
        finally { anchor?.DangerousRelease(); }
    }
}
