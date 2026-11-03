namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27bd: the security contract for a future OS-specific recovery
/// reader. Not wired to OfficeRecoveryStore until Windows/Linux/SMB probes
/// pass real offline tests. No native function is invoked by this file.
/// </summary>
internal enum RecoveryNativePlatform
{
    Windows,
    Linux,
}

/// <summary>
/// Windows: volume serial plus the full 128-bit FILE_ID_INFO identifier.
/// Linux: st_dev plus 64-bit st_ino (ObjectHigh must be zero).
/// These fields must come from OS file handles, NOT FileInfo path metadata.
/// Zero/partial SMB identity is deliberately not trusted.
/// </summary>
internal readonly record struct RecoveryNativeFileId(
    RecoveryNativePlatform Platform,
    ulong DeviceOrVolume,
    ulong ObjectLow,
    ulong ObjectHigh)
{
    internal bool IsComplete =>
        Enum.IsDefined(Platform) &&
        DeviceOrVolume != 0 &&
        (ObjectLow != 0 || ObjectHigh != 0) &&
        (Platform != RecoveryNativePlatform.Linux || ObjectHigh == 0);
}

/// <summary>
/// Only the canonical 64-character per-document recovery folder hash and a
/// single GUID-based OOXML file name can be passed to a native relative opener.
/// No rooted paths, separators, alternate data streams, '..' or arbitrary
/// manifest paths ever enter this contract.
/// </summary>
internal readonly record struct RecoveryRelativeKey
{
    internal string SourceFolderHash { get; }
    internal string CopyFileName { get; }

    private RecoveryRelativeKey(string sourceFolderHash, string copyFileName)
    {
        SourceFolderHash = sourceFolderHash;
        CopyFileName = copyFileName;
    }

    internal static RecoveryRelativeKey Create(string sourceFolderHash, string copyFileName)
    {
        if (sourceFolderHash is null || sourceFolderHash.Length != 64 ||
            !sourceFolderHash.All(IsAsciiHex))
            throw new ArgumentException(
                "Недопустимый идентификатор каталога аварийной копии.",
                nameof(sourceFolderHash));

        if (copyFileName is null || copyFileName.Length < 33 ||
            !Guid.TryParseExact(copyFileName[..32], "N", out _))
            throw new ArgumentException(
                "Недопустимое имя аварийной копии.", nameof(copyFileName));

        string suffix = copyFileName[32..];
        if (suffix is not (
            ".docx" or ".xlsx" or ".pptx" or
            ".part.docx" or ".part.xlsx" or ".part.pptx"))
            throw new ArgumentException(
                "Недопустимое расширение аварийной копии.", nameof(copyFileName));

        return new RecoveryRelativeKey(sourceFolderHash, copyFileName);
    }

    private static bool IsAsciiHex(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}

/// <summary>
/// A future OS adapter must open a trusted recovery root descriptor, then
/// resolve the source hash + copy basename relative to that descriptor with
/// no-follow semantics. Both parent and file identities must be sourced from
/// OS HANDLES at open and recheck time. The policy gate never reads paths.
/// </summary>
internal interface IRecoveryNativeIdentityAdapter
{
    RecoveryNativePlatform Platform { get; }
    bool SupportsVerifiedDirectoryRelativeOpen { get; }
    RecoveryNativeReadLease OpenReadOnlyRelative(RecoveryRelativeKey key);
}

/// <summary>
/// Caller-owned read handle plus evidence. The adapter must not present
/// path-based FileInfo observations as native handle identity. In real OS
/// adapters the first/second observations must be captured from the SAME
/// open handles, including the trusted parent directory descriptor.
/// </summary>
internal sealed class RecoveryNativeReadLease : IDisposable
{
    internal FileStream Stream { get; }
    internal RecoveryNativePlatform Platform { get; }
    internal RecoveryNativeFileId ParentAtOpen { get; }
    internal RecoveryNativeFileId ParentAtRecheck { get; }
    internal RecoveryNativeFileId FileAtOpen { get; }
    internal RecoveryNativeFileId FileAtRecheck { get; }
    internal bool OpenedRelativeToTrustedParent { get; }
    internal bool NoFollowEnforced { get; }

    internal RecoveryNativeReadLease(
        FileStream stream,
        RecoveryNativePlatform platform,
        RecoveryNativeFileId parentAtOpen,
        RecoveryNativeFileId parentAtRecheck,
        RecoveryNativeFileId fileAtOpen,
        RecoveryNativeFileId fileAtRecheck,
        bool directoryRelative,
        bool noFollow)
    {
        Stream = stream ?? throw new ArgumentNullException(nameof(stream));
        Platform = platform;
        ParentAtOpen = parentAtOpen;
        ParentAtRecheck = parentAtRecheck;
        FileAtOpen = fileAtOpen;
        FileAtRecheck = fileAtRecheck;
        OpenedRelativeToTrustedParent = directoryRelative;
        NoFollowEnforced = noFollow;
    }

    public void Dispose() => Stream.Dispose();
}

/// <summary>
/// Pure managed evidence gate. This never makes a path-based open "secure".
/// It only rejects incomplete claims from a native adapter; all production
/// adapters currently refuse the operation (and this gate is not registered
/// in OfficeRecoveryStore). Fake adapters are only used by MSTest.
/// </summary>
internal static class RecoveryNativeIdentityGate
{
    private const long MaxRecoveryCopyBytes = 128L * 1024 * 1024;

    internal static RecoveryNativeReadLease OpenVerifiedRead(
        IRecoveryNativeIdentityAdapter adapter, RecoveryRelativeKey key)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        // A default-initialized record struct must not bypass the only
        // allowed relative-name grammar before any native adapter is called.
        RecoveryRelativeKey.Create(key.SourceFolderHash, key.CopyFileName);
        if (!adapter.SupportsVerifiedDirectoryRelativeOpen)
            throw new PlatformNotSupportedException(
                "Нативное относительное открытие с no-follow не подтверждено на данной платформе.");

        RecoveryNativeReadLease lease = adapter.OpenReadOnlyRelative(key)
            ?? throw new InvalidDataException("Нативный адаптер не вернул проверенный дескриптор.");
        try
        {
            if (!Enum.IsDefined(adapter.Platform) ||
                lease.Platform != adapter.Platform ||
                !lease.OpenedRelativeToTrustedParent ||
                !lease.NoFollowEnforced ||
                !lease.ParentAtOpen.IsComplete ||
                !lease.ParentAtRecheck.IsComplete ||
                !lease.FileAtOpen.IsComplete ||
                !lease.FileAtRecheck.IsComplete ||
                lease.ParentAtOpen.Platform != adapter.Platform ||
                lease.FileAtOpen.Platform != adapter.Platform ||
                lease.ParentAtOpen != lease.ParentAtRecheck ||
                lease.FileAtOpen != lease.FileAtRecheck ||
                lease.ParentAtOpen.DeviceOrVolume != lease.FileAtOpen.DeviceOrVolume ||
                !lease.Stream.CanRead ||
                !lease.Stream.CanSeek ||
                lease.Stream.CanWrite ||
                lease.Stream.Length == 0 ||
                lease.Stream.Length > MaxRecoveryCopyBytes)
                throw new InvalidDataException(
                    "Нативная идентичность аварийной копии не подтверждена.");
            return lease;
        }
        catch
        {
            // A rejected claim must never leak an open read handle.
            lease.Dispose();
            throw;
        }
    }
}
