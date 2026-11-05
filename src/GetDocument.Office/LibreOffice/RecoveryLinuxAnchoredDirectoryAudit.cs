using System.ComponentModel;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27bu: Linux/Astra READ-ONLY ancestry observation starting at the
/// kernel's "/" in the CURRENT mount namespace. Only existing directories.
/// This does not attest the namespace itself, host policy, inherited ACLs,
/// or writer isolation. Never a capture/delete/publication authorization.
/// </summary>
internal static class RecoveryLinuxAnchoredDirectoryAudit
{
    private const long OpenAt2Syscall = 437; // Linux x86_64 and arm64
    private const int ODirectory = 0x10000;
    private const int ONoFollow = 0x20000;
    private const int OCloexec = 0x80000;
    private const ulong ResolveNoXdev = 0x01;
    private const ulong ResolveNoMagicLinks = 0x02;
    private const ulong ResolveNoSymlinks = 0x04;
    private const ulong ResolveBeneath = 0x08;
    private const int MaxDepth = 32;
    private const int MaxPathBytes = 4096;
    private const int MaxComponentBytes = 255;
    private const int MaxXattrBytes = 8192;
    private const int MaxXattrNames = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string[] ValidateAbsolutePath(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith('/'))
            throw new ArgumentException("Требуется абсолютный Linux-путь.", nameof(path));
        if (path.Length == 1 || path.Contains('\\') || path.Contains('\0') ||
            path.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("Отказ для неоднозначного Linux-пути.", nameof(path));
        try
        {
            if (StrictUtf8.GetByteCount(path) > MaxPathBytes)
                throw new ArgumentException("Linux-путь превышает лимит.", nameof(path));
            string[] parts = path[1..].Split('/');
            if (parts.Length > MaxDepth || parts.Any(part =>
                part.Length == 0 || part is "." or ".." ||
                StrictUtf8.GetByteCount(part) > MaxComponentBytes))
                throw new ArgumentException("Недопустимый компонент Linux-пути.", nameof(path));
            return parts;
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException("Путь не кодируется однозначно в UTF-8.",
                nameof(path), ex);
        }
    }

    internal static void RequireDirectoryPolicy(
        RecoveryLinuxParentEvidence item, uint effectiveUid, bool isFinal)
    {
        if (!item.FileId.IsComplete ||
            item.FileId.Platform != RecoveryNativePlatform.Linux ||
            item.MountId == 0 || (item.Mode & 0xF000) != 0x4000)
            throw new InvalidDataException("Дескриптор не подтверждён как Linux-каталог.");

        if (isFinal)
        {
            // Only a private user-owned final workspace may even be inspected.
            RecoveryPinnedLinuxParentProbe.RequirePrivateOwner(item, effectiveUid);
            return;
        }

        // The kernel "/" starts the chain in this mount namespace. Existing
        // ancestors may be owned by root or the current UID, but NO group /
        // other write bit can be present (including sticky /tmp).
        if (item.OwnerUid != 0 && item.OwnerUid != effectiveUid)
            throw new InvalidDataException("Чужой владелец каталога предка.");
        if ((item.Mode & 0x0012) != 0) // 0022 octal: group/other write
            throw new InvalidDataException("Групповая или общая запись в каталоге предка.");
    }

    internal static void RequireStableChain(
        RecoveryLinuxParentEvidence parentAtOpen,
        RecoveryLinuxParentEvidence parentRecheck,
        RecoveryLinuxParentEvidence child)
    {
        if (parentAtOpen != parentRecheck ||
            !child.FileId.IsComplete ||
            parentAtOpen.MountId != child.MountId ||
            parentAtOpen.FileId.DeviceOrVolume != child.FileId.DeviceOrVolume)
            throw new InvalidDataException(
                "Родитель изменился или путь пересёк границу монтирования.");
    }

    internal static RecoveryLinuxXattrEvidence ParseXattrNames(
        byte[] source, int bytesRead)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (bytesRead < 0 || bytesRead > source.Length ||
            bytesRead > MaxXattrBytes)
            throw new InvalidDataException("Размер списка xattr недопустим.");
        if (bytesRead == 0)
            return new RecoveryLinuxXattrEvidence(0, false, false, false,
                Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())));

        if (source[bytesRead - 1] != 0)
            throw new InvalidDataException("Список xattr не завершён NUL.");
        int count = 0;
        bool acl = false, security = false, other = false;
        var unique = new HashSet<string>(StringComparer.Ordinal);
        int start = 0;
        for (int pos = 0; pos < bytesRead; pos++)
        {
            if (source[pos] != 0) continue;
            if (pos == start || ++count > MaxXattrNames)
                throw new InvalidDataException("Пустое или избыточное имя xattr.");
            string name;
            try
            {
                name = StrictUtf8.GetString(source, start, pos - start);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("Имя xattr не является корректным UTF-8.", ex);
            }
            if (!unique.Add(name))
                throw new InvalidDataException("Повторное имя xattr.");
            if (name is "system.posix_acl_access" or "system.posix_acl_default")
                acl = true;
            else if (name.StartsWith("security.", StringComparison.Ordinal) ||
                     name.StartsWith("trusted.", StringComparison.Ordinal))
                security = true;
            else
                other = true;
            start = pos + 1;
        }
        // Kernel order is unspecified: canonicalize before digesting. This is
        // a change detector for visible xattr NAMES only, not their values.
        string canonical = string.Join("\0", unique.OrderBy(x => x,
            StringComparer.Ordinal)) + "\0";
        string digest = Convert.ToHexString(
            SHA256.HashData(StrictUtf8.GetBytes(canonical)));
        return new RecoveryLinuxXattrEvidence(count, acl, security, other, digest);
    }

    internal static RecoveryLinuxAnchoredDirectoryLease InspectExisting(
        string absoluteDirectory, CancellationToken cancellationToken = default)
    {
        string[] parts = ValidateAbsolutePath(absoluteDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() || !BitConverter.IsLittleEndian ||
            RuntimeInformation.ProcessArchitecture is not
                (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException(
                "Linux/Astra read-only openat2/statx audit недоступен на этой платформе.");

        var owned = new List<SafeFileHandle>(parts.Length + 1);
        var records = new List<RecoveryLinuxAnchoredNode>(parts.Length + 1);
        bool transferred = false;
        try
        {
            // open("/") is the only pathname lookup. It is anchored to this
            // process's current mount namespace and does not prove that the
            // namespace is host-trusted (containers/userns/bind mounts).
            SafeFileHandle root = OpenNamespaceRoot();
            owned.Add(root);
            uint euid = GetEffectiveUid();
            var first = ObserveDirectory(root);
            RequireDirectoryPolicy(first.Metadata, euid, isFinal: false);
            records.Add(first);
            for (int i = 0; i < parts.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parent = owned[^1];
                RecoveryLinuxParentEvidence before =
                    RecoveryPinnedLinuxParentProbe.ReadExistingDirectoryEvidence(parent);
                RequireDirectoryPolicy(before, euid, isFinal: false);
                SafeFileHandle child = OpenChild(parent, parts[i]);
                owned.Add(child); // owned even if subsequent statx/xattr fails
                var observed = ObserveDirectory(child);
                RequireDirectoryPolicy(observed.Metadata, euid,
                    isFinal: i == parts.Length - 1);
                RecoveryLinuxParentEvidence rechecked =
                    RecoveryPinnedLinuxParentProbe.ReadExistingDirectoryEvidence(parent);
                RequireStableChain(before, rechecked, observed.Metadata);
                records.Add(observed);
            }

            // Reobserve every held descriptor, not filesystem pathnames.
            // This is still a point-in-time read; post-check rename and
            // ACL/xattr changes are not prevented by this lease.
            for (int i = 0; i < owned.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = ObserveDirectory(owned[i]);
                if (current != records[i])
                    throw new InvalidDataException(
                        "statx/xattr каталога изменились во время обследования.");
                RequireDirectoryPolicy(current.Metadata, euid,
                    isFinal: i == owned.Count - 1);
            }

            var lease = new RecoveryLinuxAnchoredDirectoryLease(
                owned.ToArray(), records.AsReadOnly());
            transferred = true;
            return lease;
        }
        finally
        {
            if (!transferred)
                for (int i = owned.Count - 1; i >= 0; --i)
                    owned[i].Dispose();
        }
    }

    private static RecoveryLinuxAnchoredNode ObserveDirectory(SafeFileHandle handle)
    {
        RecoveryLinuxParentEvidence evidence =
            RecoveryPinnedLinuxParentProbe.ReadExistingDirectoryEvidence(handle);
        RecoveryLinuxXattrEvidence attributes = ReadXattrNames(handle);
        return new RecoveryLinuxAnchoredNode(evidence, attributes);
    }

    private static SafeFileHandle OpenNamespaceRoot()
    {
        int fd = Open("/", ODirectory | ONoFollow | OCloexec);
        if (fd < 0)
            throw new IOException("Нельзя закрепить Linux mount namespace root /.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    private static SafeFileHandle OpenChild(SafeFileHandle heldParent, string basename)
    {
        // All these fd are solely owned by this call and remain alive until
        // lease disposal. openat2 has no insecure openat()/File.Open fallback.
        var how = new OpenHow
        {
            Flags = (ulong)(ODirectory | ONoFollow | OCloexec),
            Mode = 0,
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
                "libc syscall(openat2) отсутствует.", ex);
        }
        if (fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            if (errno is 38 or 22) // ENOSYS/EINVAL unsupported kernel/flags
                throw new PlatformNotSupportedException(
                    "Ядро Linux/Astra не поддерживает требуемый openat2.");
            throw new IOException("Нельзя открыть каталог без ссылок и смены mount.",
                new Win32Exception(errno));
        }
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    private static RecoveryLinuxXattrEvidence ReadXattrNames(SafeFileHandle handle)
    {
        int fd = handle.DangerousGetHandle().ToInt32();
        long required = FListXattr(fd, null, 0);
        if (required < 0)
            throw new IOException("flistxattr недоступен на каталоге.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        if (required > MaxXattrBytes)
            throw new InvalidDataException("Список xattr превышает лимит 8 KiB.");

        var bytes = new byte[Math.Max(1, (int)required)];
        long read = FListXattr(fd, bytes, (ulong)bytes.Length);
        if (read < 0)
            throw new IOException("Список xattr изменён или недоступен.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        // Fail closed if a race increases the result after the sizing probe.
        if (read > required)
            throw new InvalidDataException("flistxattr изменился между чтениями.");
        return ParseXattrNames(bytes, checked((int)read));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow
    {
        internal ulong Flags;
        internal ulong Mode;
        internal ulong Resolve;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int flags);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long SyscallOpenAt2(
        long number, int directoryFd,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        ref OpenHow how, nuint size);

    [DllImport("libc", EntryPoint = "flistxattr", SetLastError = true)]
    private static extern long FListXattr(
        int fd, [Out] byte[]? names, ulong capacity);

    [DllImport("libc", EntryPoint = "geteuid", ExactSpelling = true)]
    private static extern uint GetEffectiveUid();
}

internal readonly record struct RecoveryLinuxXattrEvidence(
    int NameCount, bool PosixAclPresent, bool SecurityAttributePresent,
    bool OtherUnreviewedAttributesPresent, string NamesSha256)
{
    // Listing may omit attributes inaccessible to this user; no security
    // authorization can be inferred from an apparently empty xattr list.
    internal bool CanAuthorizeCapture => false;
}

internal readonly record struct RecoveryLinuxAnchoredNode(
    RecoveryLinuxParentEvidence Metadata, RecoveryLinuxXattrEvidence Xattrs);

/// <summary>
/// Retains every directory handle in the inspected chain until Dispose.
/// This is purely read-only diagnostic evidence, not a path pin against all
/// namespace changes and not an authority to create or unlink anything.
/// </summary>
internal sealed class RecoveryLinuxAnchoredDirectoryLease : IDisposable
{
    private SafeFileHandle[]? _handles;
    internal IReadOnlyList<RecoveryLinuxAnchoredNode> Nodes { get; }
    internal bool CanAuthorizeCapture => false;

    internal RecoveryLinuxAnchoredDirectoryLease(
        SafeFileHandle[] handles, IReadOnlyList<RecoveryLinuxAnchoredNode> nodes)
    {
        _handles = handles;
        Nodes = nodes;
    }

    public void Dispose()
    {
        var handles = Interlocked.Exchange(ref _handles, null);
        if (handles is null) return;
        for (int i = handles.Length - 1; i >= 0; --i)
            handles[i].Dispose();
    }
}
