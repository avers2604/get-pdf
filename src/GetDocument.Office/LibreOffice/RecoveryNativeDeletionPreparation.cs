namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27bl. A strictly typed candidate for a FUTURE native relative
/// deletion mechanism. No absolute paths or arbitrary names are accepted.
/// Creating a key does not establish that a file belongs to us.
/// </summary>
internal readonly record struct RecoveryNativeDeleteKey
{
    internal string SourceFolderHash { get; }
    internal string ArtifactName { get; }

    private RecoveryNativeDeleteKey(string sourceFolderHash, string artifactName)
    {
        SourceFolderHash = sourceFolderHash;
        ArtifactName = artifactName;
    }

    internal static RecoveryNativeDeleteKey Create(
        string sourceFolderHash, string artifactName)
    {
        if (sourceFolderHash is null || sourceFolderHash.Length != 64 ||
            !sourceFolderHash.All(c =>
                c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
            throw new ArgumentException("Неверный идентификатор каталога аварийных копий.",
                nameof(sourceFolderHash));

        if (artifactName is null || artifactName.Length <= 32 ||
            !Guid.TryParseExact(artifactName[..32], "N", out _))
            throw new ArgumentException("Недопустимое имя удаляемого артефакта.",
                nameof(artifactName));

        string suffix = artifactName[32..];
        if (suffix is not (".docx" or ".xlsx" or ".pptx" or
                           ".part.docx" or ".part.xlsx" or ".part.pptx" or
                           ".json" or ".json.tmp"))
            throw new ArgumentException("Недопустимое расширение артефакта.",
                nameof(artifactName));

        return new RecoveryNativeDeleteKey(sourceFolderHash, artifactName);
    }
}

/// <summary>
/// Structurally consistent OS-HANDLE evidence is necessary but NOT sufficient
/// for a safe unlink. The current records are merely adapter claims; only
/// a tested native implementation could attest them. There is no delete API.
/// </summary>
internal readonly record struct RecoveryNativeDeleteEvidence(
    RecoveryNativePlatform Platform,
    RecoveryNativeFileId ParentAtOpen,
    RecoveryNativeFileId ParentBeforeDelete,
    RecoveryNativeFileId FileAtOpen,
    RecoveryNativeFileId FileBeforeDelete,
    bool OpenedRelativeToTrustedRoot,
    bool NoFollowEnforced,
    bool ParentWriteIsolationConfirmed,
    bool FileHandlePinned);

/// <summary>
/// Future adapter MUST observe identity from live directory/file handles.
/// Linux unlinkat(dirfd,name) alone cannot atomically bind the deletion
/// to an inode under a hostile rename race. Do not enable until the private
/// parent is isolated, the race is proven closed and platform tests pass.
/// </summary>
internal interface IRecoveryNativeDeleteObservationAdapter
{
    RecoveryNativePlatform Platform { get; }
    bool SupportsHandleBoundDeleteObservation { get; }
    RecoveryNativeDeleteEvidence ReadHeldHandleEvidence(RecoveryNativeDeleteKey key);
}

/// <summary>
/// A pure planning-only rejection gate; it never invokes unlink/Dispose-delete,
/// File.Delete, File.Move, shell commands or OS-native delete calls.
/// Even a successful result cannot be used as delete authorization.
/// </summary>
internal static class RecoveryNativeDeletionPreparationGate
{
    internal static RecoveryNativeDeleteEvidence InspectForPlanning(
        IRecoveryNativeDeleteObservationAdapter adapter, RecoveryNativeDeleteKey key)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        // A default-initialized record struct must fail before any OS call.
        RecoveryNativeDeleteKey.Create(key.SourceFolderHash, key.ArtifactName);
        if (!adapter.SupportsHandleBoundDeleteObservation)
            throw new PlatformNotSupportedException(
                "Нативное наблюдение дескрипторов перед удалением не аттестовано.");

        RecoveryNativeDeleteEvidence evidence = adapter.ReadHeldHandleEvidence(key);
        if (!Enum.IsDefined(adapter.Platform) ||
            evidence.Platform != adapter.Platform ||
            !evidence.ParentAtOpen.IsComplete ||
            !evidence.ParentBeforeDelete.IsComplete ||
            !evidence.FileAtOpen.IsComplete ||
            !evidence.FileBeforeDelete.IsComplete ||
            evidence.ParentAtOpen.Platform != adapter.Platform ||
            evidence.FileAtOpen.Platform != adapter.Platform ||
            evidence.ParentAtOpen != evidence.ParentBeforeDelete ||
            evidence.FileAtOpen != evidence.FileBeforeDelete ||
            evidence.ParentAtOpen.DeviceOrVolume != evidence.FileAtOpen.DeviceOrVolume ||
            !evidence.OpenedRelativeToTrustedRoot ||
            !evidence.NoFollowEnforced ||
            !evidence.ParentWriteIsolationConfirmed ||
            !evidence.FileHandlePinned)
            throw new InvalidDataException(
                "Требуемая нативная идентичность объекта перед удалением не подтверждена.");

        // Audit observation only: this method intentionally offers no delete
        // capability, no path and no authority to mutate a document.
        return evidence;
    }
}

internal sealed class WindowsRecoveryNativeDeleteObservationAdapter :
    IRecoveryNativeDeleteObservationAdapter
{
    public RecoveryNativePlatform Platform => RecoveryNativePlatform.Windows;
    public bool SupportsHandleBoundDeleteObservation => false;
    public RecoveryNativeDeleteEvidence ReadHeldHandleEvidence(RecoveryNativeDeleteKey key) =>
        throw new PlatformNotSupportedException(
            "Нативная Windows file-ID-bound очистка не внедрена.");
}

internal sealed class LinuxRecoveryNativeDeleteObservationAdapter :
    IRecoveryNativeDeleteObservationAdapter
{
    public RecoveryNativePlatform Platform => RecoveryNativePlatform.Linux;
    public bool SupportsHandleBoundDeleteObservation => false;
    public RecoveryNativeDeleteEvidence ReadHeldHandleEvidence(RecoveryNativeDeleteKey key) =>
        throw new PlatformNotSupportedException(
            "Нативная Linux identity-bound очистка не внедрена.");
}
