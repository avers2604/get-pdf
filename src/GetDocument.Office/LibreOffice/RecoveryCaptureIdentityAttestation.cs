namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27bn: a full absolute root path is a REQUEST to an eventual OS
/// adapter, never evidence that the root or a lock-file belongs to GET.
/// No native handle is opened and no path is made trusted by this type.
/// </summary>
internal readonly record struct RecoveryCaptureIdentityRequest
{
    internal string RecoveryRoot { get; }

    private RecoveryCaptureIdentityRequest(string recoveryRoot) =>
        RecoveryRoot = recoveryRoot;

    internal static RecoveryCaptureIdentityRequest Create(string recoveryRoot)
    {
        if (string.IsNullOrWhiteSpace(recoveryRoot) ||
            !Path.IsPathFullyQualified(recoveryRoot))
            throw new ArgumentException(
                "Требуется абсолютный каталог аварийного восстановления.",
                nameof(recoveryRoot));
        return new RecoveryCaptureIdentityRequest(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(recoveryRoot)));
    }
}

/// <summary>
/// Claims read from native handles at multiple points. A rechecked lock-file
/// NAME must resolve to the SAME OS file ID as the held handle. This demands
/// native no-follow + directory-relative mechanisms, not FileInfo metadata.
/// </summary>
internal readonly record struct RecoveryCaptureIdentityEvidence(
    RecoveryNativePlatform Platform,
    RecoveryNativeFileId RootAtOpen,
    RecoveryNativeFileId RootAtRecheck,
    RecoveryNativeFileId LockAtOpen,
    RecoveryNativeFileId LockAtRecheck,
    RecoveryNativeFileId LockNameAtRecheck,
    bool RootOpenedRelativeToTrustedAnchor,
    bool NoFollowEnforced,
    bool ParentWriterIsolationConfirmed,
    bool LockHandlePinned,
    bool LockRangeHeld);

/// <summary>
/// Pure planning contract for future Windows FileIdInfo + directory ACL/handle
/// and Astra Linux openat2/fstatat + protected directory writer isolation.
/// Real adapters MUST retain the root & lock handles through validation.
/// Neither a fake result nor a consistent identity record grants a lock,
/// directory deletion or file deletion capability.
/// </summary>
internal interface IRecoveryCaptureIdentityAdapter
{
    RecoveryNativePlatform Platform { get; }
    bool SupportsVerifiedRootAndLockIdentity { get; }
    RecoveryCaptureIdentityEvidence ObserveHeldIdentity(
        RecoveryCaptureIdentityRequest root);
}

internal static class RecoveryCaptureIdentityGate
{
    internal static RecoveryCaptureIdentityEvidence InspectForPlanning(
        IRecoveryCaptureIdentityAdapter adapter,
        RecoveryCaptureIdentityRequest root)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        RecoveryCaptureIdentityRequest.Create(root.RecoveryRoot);
        if (!adapter.SupportsVerifiedRootAndLockIdentity)
            throw new PlatformNotSupportedException(
                "Нативная идентичность каталога и lock-файла не подтверждена.");

        var evidence = adapter.ObserveHeldIdentity(root);
        if (!Enum.IsDefined(adapter.Platform) ||
            evidence.Platform != adapter.Platform ||
            !evidence.RootAtOpen.IsComplete ||
            !evidence.RootAtRecheck.IsComplete ||
            !evidence.LockAtOpen.IsComplete ||
            !evidence.LockAtRecheck.IsComplete ||
            !evidence.LockNameAtRecheck.IsComplete ||
            evidence.RootAtOpen.Platform != adapter.Platform ||
            evidence.RootAtRecheck.Platform != adapter.Platform ||
            evidence.LockAtOpen.Platform != adapter.Platform ||
            evidence.LockAtRecheck.Platform != adapter.Platform ||
            evidence.LockNameAtRecheck.Platform != adapter.Platform ||
            evidence.RootAtOpen != evidence.RootAtRecheck ||
            evidence.LockAtOpen != evidence.LockAtRecheck ||
            evidence.LockAtOpen != evidence.LockNameAtRecheck ||
            evidence.RootAtOpen.DeviceOrVolume != evidence.LockAtOpen.DeviceOrVolume ||
            !evidence.RootOpenedRelativeToTrustedAnchor ||
            !evidence.NoFollowEnforced ||
            !evidence.ParentWriterIsolationConfirmed ||
            !evidence.LockHandlePinned ||
            !evidence.LockRangeHeld)
            throw new InvalidDataException(
                "Нативный корень/lock-файл изменён, не проверен или не изолирован.");

        // This is informational evidence; NO native adapter, lock promotion,
        // unlink or production admission is invoked here.
        return evidence;
    }
}

/// <summary>
/// Deliberately disabled: do not claim that managed path-based FileStream,
/// FileInfo, ACL and local locking can prove native handle identity.
/// </summary>
internal sealed class WindowsRecoveryCaptureIdentityAdapter :
    IRecoveryCaptureIdentityAdapter
{
    public RecoveryNativePlatform Platform => RecoveryNativePlatform.Windows;
    public bool SupportsVerifiedRootAndLockIdentity => false;
    public RecoveryCaptureIdentityEvidence ObserveHeldIdentity(
        RecoveryCaptureIdentityRequest root) =>
        throw new PlatformNotSupportedException(
            "Нативная проверка FileIdInfo/root handle Windows не внедрена.");
}

internal sealed class LinuxRecoveryCaptureIdentityAdapter :
    IRecoveryCaptureIdentityAdapter
{
    public RecoveryNativePlatform Platform => RecoveryNativePlatform.Linux;
    public bool SupportsVerifiedRootAndLockIdentity => false;
    public RecoveryCaptureIdentityEvidence ObserveHeldIdentity(
        RecoveryCaptureIdentityRequest root) =>
        throw new PlatformNotSupportedException(
            "Нативная проверка openat2/fstat root handle Astra Linux не внедрена.");
}
