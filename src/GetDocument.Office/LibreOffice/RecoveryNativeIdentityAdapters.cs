namespace GetDocument.Office.LibreOffice;

/// <summary>
/// Windows File ID / handle-relative recovery adapter placeholder.
/// REQUIRED before enabling: trusted directory handle; final-component
/// reparse refusal; FILE_ID_INFO (volume serial + 128-bit FileId) from the
/// actual opened file handle, and verified parent handle identity.
/// Windows GetFileInformationByHandle[Ex] does not by itself establish
/// a safe no-follow relative open. SMB may return partial/unstable IDs.
/// No native call or fallback is made in this unverified stage.
/// </summary>
internal sealed class WindowsRecoveryNativeIdentityAdapter : IRecoveryNativeIdentityAdapter
{
    public RecoveryNativePlatform Platform => RecoveryNativePlatform.Windows;
    public bool SupportsVerifiedDirectoryRelativeOpen => false;

    public RecoveryNativeReadLease OpenReadOnlyRelative(RecoveryRelativeKey key) =>
        throw new PlatformNotSupportedException(
            "Windows File ID и открытие относительно доверенного дескриптора ещё не проверены.");
}

/// <summary>
/// Linux/Astra adapter placeholder. REQUIRED before enabling: trusted root
/// dirfd; openat2 RESOLVE_BENEATH|RESOLVE_NO_SYMLINKS (where supported) or
/// an independently reviewed per-component openat/O_NOFOLLOW fallback;
/// fstat(st_dev,st_ino) from open directory/file descriptors; reject missing
/// support and network/SMB identity ambiguity instead of path-based fallback.
/// Linux openat2 is kernel-dependent and must be proven on target Astra
/// builds, not inferred from an OS or distribution name.
/// </summary>
internal sealed class LinuxRecoveryNativeIdentityAdapter : IRecoveryNativeIdentityAdapter
{
    public RecoveryNativePlatform Platform => RecoveryNativePlatform.Linux;
    public bool SupportsVerifiedDirectoryRelativeOpen => false;

    public RecoveryNativeReadLease OpenReadOnlyRelative(RecoveryRelativeKey key) =>
        throw new PlatformNotSupportedException(
            "Linux inode и no-follow openat/openat2 ещё не подтверждены на целевой Astra Linux.");
}
