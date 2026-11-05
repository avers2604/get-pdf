using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27bw. Linux-only READ-ONLY observation that named recovery COPY
/// and MANIFEST currently refer to the same objects as held file handles.
/// A matching name is NOT a native mutation capability: rename/unlink of
/// names can race after the last observation. No production wiring.
/// </summary>
internal static class RecoveryLinuxNamedPairProbe
{
    private const long OpenAt2Syscall = 437; // x86_64 and aarch64 Linux UAPI
    private const ulong OPath = 0x200000;
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
    private const uint StatxNlink = 0x0004;
    private const uint StatxUid = 0x0008;
    private const uint StatxIno = 0x0100;
    private const uint StatxMntId = 0x1000;
    private const int StatxBytes = 256;
    private const ushort RegularFileType = 0x8000;
    private const ushort Private0600 = 0x0180;

    internal static bool CanAuthorizePublish => false;
    internal static bool CanAuthorizeDelete => false;

    internal static RecoveryLinuxNamedPairNames ValidatePairNames(
        string copyName, string manifestName)
    {
        const int nonceLength = 32;
        if (copyName is null || manifestName is null ||
            copyName.Length is not (37 or 38) || manifestName.Length != 37)
            throw new ArgumentException("Неизвестная грамматика имён copy/manifest.");

        string nonce = copyName[..nonceLength];
        if (!nonce.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !Guid.TryParseExact(nonce, "N", out _) ||
            manifestName != nonce + ".json")
            throw new ArgumentException("Несовпадающий или некорректный GUID пары.");

        string extension = copyName[nonceLength..];
        if (extension is not (".docx" or ".xlsx" or ".pptx"))
            throw new ArgumentException("Недопустимый формат аварийной копии.");

        return new RecoveryLinuxNamedPairNames(nonce, extension);
    }

    /// <summary>
    /// Parse ONLY a complete Linux 256-byte statx of an existing regular file
    /// referenced by a held handle; reject symlink even if O_PATH|O_NOFOLLOW
    /// returned its own descriptor. Require owner-only 0600 and nlink==1.
    /// </summary>
    internal static RecoveryLinuxRegularEntryObservation ParseRegularStatx(
        byte[] data, uint expectedEffectiveUid)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length != StatxBytes)
            throw new ArgumentException("Требуется 256-байтовый statx.", nameof(data));

        var b = data.AsSpan();
        uint mask = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(0, 4));
        const uint required = StatxType | StatxMode | StatxNlink |
            StatxUid | StatxIno | StatxMntId;
        if ((mask & required) != required)
            throw new InvalidDataException("Неполный statx файла.");

        uint links = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(16, 4));
        uint owner = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(20, 4));
        ushort mode = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(28, 2));
        ulong mount = BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(144, 8));
        if ((mode & 0xF000) != RegularFileType ||
            (mode & 0x0FFF) != Private0600 ||
            owner != expectedEffectiveUid || links != 1 || mount == 0)
            throw new InvalidDataException(
                "Объект не подтверждён как приватный 0600 regular file с одним именем.");

        RecoveryNativeFileId id = RecoveryNativeHandleIdProbe.ParseLinuxStatx(data);
        return new RecoveryLinuxRegularEntryObservation(id, mount, owner, links);
    }

    /// <summary>
    /// A pure comparison of observations. "MatchedAtObservation" NEVER
    /// permits any write, rename, unlink or cleanup operation.
    /// </summary>
    internal static RecoveryLinuxNamedPairFinding Evaluate(
        RecoveryLinuxNamedPairObservation pair)
    {
        var all = new[]
        {
            pair.CopyHeld, pair.ManifestHeld, pair.CopyNamed,
            pair.ManifestNamed, pair.CopyHeldAtRecheck,
            pair.ManifestHeldAtRecheck, pair.CopyNamedAtRecheck,
            pair.ManifestNamedAtRecheck
        };
        if (!pair.ParentAtOpen.FileId.IsComplete ||
            !pair.ParentAtRecheck.FileId.IsComplete ||
            pair.ParentAtOpen.FileId.Platform != RecoveryNativePlatform.Linux ||
            pair.ParentAtRecheck.FileId.Platform != RecoveryNativePlatform.Linux ||
            all.Any(x => !x.Id.IsComplete ||
                x.Id.Platform != RecoveryNativePlatform.Linux ||
                x.MountId == 0 || x.LinkCount != 1))
            return RecoveryLinuxNamedPairFinding.IncompleteIdentity;

        if (pair.ParentAtOpen != pair.ParentAtRecheck)
            return RecoveryLinuxNamedPairFinding.ParentChanged;

        ulong volume = pair.ParentAtOpen.FileId.DeviceOrVolume;
        ulong mount = pair.ParentAtOpen.MountId;
        uint uid = pair.ParentAtOpen.OwnerUid;
        if (mount == 0 || all.Any(x => x.Id.DeviceOrVolume != volume ||
                 x.MountId != mount || x.OwnerUid != uid))
            return RecoveryLinuxNamedPairFinding.CrossMountOrDevice;

        if (pair.CopyHeld.Id == pair.ManifestHeld.Id)
            return RecoveryLinuxNamedPairFinding.AliasedCopyAndManifest;

        if (pair.CopyHeld != pair.CopyNamed ||
            pair.ManifestHeld != pair.ManifestNamed ||
            pair.CopyHeld != pair.CopyHeldAtRecheck ||
            pair.ManifestHeld != pair.ManifestHeldAtRecheck ||
            pair.CopyHeld != pair.CopyNamedAtRecheck ||
            pair.ManifestHeld != pair.ManifestNamedAtRecheck)
            return RecoveryLinuxNamedPairFinding.NameDoesNotMatchHeldHandle;

        return RecoveryLinuxNamedPairFinding.MatchedAtObservation;
    }

    internal static RecoveryLinuxNamedPairObservation ObserveExistingPair(
        SafeFileHandle heldParent, SafeFileHandle heldCopy,
        SafeFileHandle heldManifest, string copyName, string manifestName,
        CancellationToken cancellationToken = default)
    {
        ValidateHandle(heldParent);
        ValidateHandle(heldCopy);
        ValidateHandle(heldManifest);
        var names = ValidatePairNames(copyName, manifestName);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() || !BitConverter.IsLittleEndian ||
            RuntimeInformation.ProcessArchitecture is not
                (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException(
                "read-only openat2/statx поддерживает только аттестованные Linux-архитектуры.");

        bool parentPinned = false, copyPinned = false, manifestPinned = false;
        try
        {
            heldParent.DangerousAddRef(ref parentPinned);
            heldCopy.DangerousAddRef(ref copyPinned);
            heldManifest.DangerousAddRef(ref manifestPinned);
            uint uid = GetEffectiveUserId();

            RecoveryLinuxParentEvidence before =
                RecoveryPinnedLinuxParentProbe.ReadExistingDirectoryEvidence(heldParent);
            RecoveryPinnedLinuxParentProbe.RequirePrivateOwner(before, uid);

            RecoveryLinuxRegularEntryObservation copyHeld =
                ReadRegularHeld(heldCopy, uid);
            RecoveryLinuxRegularEntryObservation manifestHeld =
                ReadRegularHeld(heldManifest, uid);
            cancellationToken.ThrowIfCancellationRequested();

            // O_PATH+O_NOFOLLOW may open the LINK ITSELF. The statx parser
            // MUST reject all non-regular types, even with NO_SYMLINKS.
            using SafeFileHandle namedCopy =
                OpenRelativeReadOnly(heldParent, names.Nonce + names.Extension);
            using SafeFileHandle namedManifest =
                OpenRelativeReadOnly(heldParent, names.Nonce + ".json");
            var copyNamed = ReadRegularHeld(namedCopy, uid);
            var manifestNamed = ReadRegularHeld(namedManifest, uid);
            cancellationToken.ThrowIfCancellationRequested();

            // Independent reopens catch a name replaced DURING observation.
            // Another rename immediately AFTER these reads remains possible.
            using SafeFileHandle laterCopy =
                OpenRelativeReadOnly(heldParent, names.Nonce + names.Extension);
            using SafeFileHandle laterManifest =
                OpenRelativeReadOnly(heldParent, names.Nonce + ".json");
            var copyNamedLater = ReadRegularHeld(laterCopy, uid);
            var manifestNamedLater = ReadRegularHeld(laterManifest, uid);
            var copyHeldLater = ReadRegularHeld(heldCopy, uid);
            var manifestHeldLater = ReadRegularHeld(heldManifest, uid);

            RecoveryLinuxParentEvidence after =
                RecoveryPinnedLinuxParentProbe.ReadExistingDirectoryEvidence(heldParent);
            RecoveryPinnedLinuxParentProbe.RequirePrivateOwner(after, uid);
            if (GetEffectiveUserId() != uid)
                throw new InvalidDataException("Эффективный UID изменился во время проверки.");
            cancellationToken.ThrowIfCancellationRequested();

            var observation = new RecoveryLinuxNamedPairObservation(
                before, after, copyHeld, manifestHeld,
                copyNamed, manifestNamed, copyHeldLater, manifestHeldLater,
                copyNamedLater, manifestNamedLater);
            if (Evaluate(observation) != RecoveryLinuxNamedPairFinding.MatchedAtObservation)
                throw new InvalidDataException(
                    "Имя пары, том или held File ID изменились при read-only проверке.");
            return observation;
        }
        finally
        {
            if (manifestPinned) heldManifest.DangerousRelease();
            if (copyPinned) heldCopy.DangerousRelease();
            if (parentPinned) heldParent.DangerousRelease();
        }
    }

    private static RecoveryLinuxRegularEntryObservation ReadRegularHeld(
        SafeFileHandle handle, uint effectiveUid)
    {
        // Caller holds the wrapper pinned for the entire probe or owns
        // this newly opened SafeFileHandle in the lexical using scope.
        byte[] bytes = new byte[StatxBytes];
        if (Statx(handle.DangerousGetHandle().ToInt32(), "",
                AtEmptyPath | AtSymlinkNoFollow,
                StatxType | StatxMode | StatxNlink | StatxUid |
                StatxIno | StatxMntId, bytes) != 0)
            throw new IOException("statx не подтвердил открытый файл.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        return ParseRegularStatx(bytes, effectiveUid);
    }

    private static SafeFileHandle OpenRelativeReadOnly(
        SafeFileHandle parent, string name)
    {
        var how = new OpenHow
        {
            Flags = OPath | ONoFollow | OCloexec,
            Mode = 0,
            Resolve = ResolveBeneath | ResolveNoSymlinks |
                      ResolveNoMagicLinks | ResolveNoXdev
        };
        long fd;
        try
        {
            fd = SyscallOpenAt2(OpenAt2Syscall,
                parent.DangerousGetHandle().ToInt32(), name,
                ref how, (nuint)Marshal.SizeOf<OpenHow>());
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new PlatformNotSupportedException(
                "В libc недоступен syscall(openat2).", ex);
        }
        if (fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            if (errno is 38 or 22) // ENOSYS/EINVAL
                throw new PlatformNotSupportedException(
                    "Ядро Linux/Astra не поддерживает требуемый openat2.");
            throw new IOException("Нельзя открыть basename относительно parent fd.",
                new Win32Exception(errno));
        }
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    private static void ValidateHandle(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsClosed || handle.IsInvalid)
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
        long syscallNumber, int directoryFd,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        ref OpenHow how, nuint size);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string emptyPath,
        int flags, uint mask, [Out] byte[] stat);

    [DllImport("libc", EntryPoint = "geteuid", ExactSpelling = true)]
    private static extern uint GetEffectiveUserId();
}

internal readonly record struct RecoveryLinuxNamedPairNames(
    string Nonce, string Extension);

internal readonly record struct RecoveryLinuxRegularEntryObservation(
    RecoveryNativeFileId Id, ulong MountId, uint OwnerUid, uint LinkCount);

internal enum RecoveryLinuxNamedPairFinding
{
    IncompleteIdentity,
    ParentChanged,
    CrossMountOrDevice,
    AliasedCopyAndManifest,
    NameDoesNotMatchHeldHandle,
    MatchedAtObservation
}

internal readonly record struct RecoveryLinuxNamedPairObservation(
    RecoveryLinuxParentEvidence ParentAtOpen,
    RecoveryLinuxParentEvidence ParentAtRecheck,
    RecoveryLinuxRegularEntryObservation CopyHeld,
    RecoveryLinuxRegularEntryObservation ManifestHeld,
    RecoveryLinuxRegularEntryObservation CopyNamed,
    RecoveryLinuxRegularEntryObservation ManifestNamed,
    RecoveryLinuxRegularEntryObservation CopyHeldAtRecheck,
    RecoveryLinuxRegularEntryObservation ManifestHeldAtRecheck,
    RecoveryLinuxRegularEntryObservation CopyNamedAtRecheck,
    RecoveryLinuxRegularEntryObservation ManifestNamedAtRecheck)
{
    internal bool CanAuthorizeMutation => false;
}
