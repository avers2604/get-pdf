using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27ca / BLOCK 2: read-only restart comparison of six CALLER-OWNED
/// open native handles. No pathname, directory enumeration, handle creation,
/// file contents, write, flush, rename, unlink or deletion occurs here.
/// Windows FILE_ID_INFO and Linux statx(AT_EMPTY_PATH) are delegated to the
/// existing probe. OS identity alone does NOT prove the directory entry
/// still names this object (or that the caller supplied a real directory).
/// </summary>
internal static class RecoveryRestartHeldHandleReadback
{
    internal static bool NativeNameBindingVerificationEnabled => false;

    internal static RecoveryHeldHandleReadbackResult Observe(
        SafeFileHandle parentAtOpen,
        SafeFileHandle parentAtRecheck,
        SafeFileHandle copyAtOpen,
        SafeFileHandle copyAtRecheck,
        SafeFileHandle manifestAtOpen,
        SafeFileHandle manifestAtRecheck,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Reject all invalid handles BEFORE the first native observation.
        // Handles remain owned by the caller; never dispose, close or duplicate.
        SafeFileHandle[] handles =
        {
            parentAtOpen, parentAtRecheck, copyAtOpen, copyAtRecheck,
            manifestAtOpen, manifestAtRecheck
        };
        foreach (SafeFileHandle handle in handles)
        {
            ArgumentNullException.ThrowIfNull(handle);
            if (handle.IsClosed || handle.IsInvalid)
                throw new ObjectDisposedException(nameof(handle),
                    "Закрыт дескриптор повторной проверки.");
        }

        // Capture open IDs first, then recheck IDs. Each native read is
        // handle-bound, never a FileInfo(path) or following symlinks.
        var parent = RecoveryNativeHandleIdProbe.ReadFromExistingHandle(parentAtOpen);
        cancellationToken.ThrowIfCancellationRequested();
        var copy = RecoveryNativeHandleIdProbe.ReadFromExistingHandle(copyAtOpen);
        cancellationToken.ThrowIfCancellationRequested();
        var manifest = RecoveryNativeHandleIdProbe.ReadFromExistingHandle(manifestAtOpen);
        cancellationToken.ThrowIfCancellationRequested();
        var afterParent =
            RecoveryNativeHandleIdProbe.ReadFromExistingHandle(parentAtRecheck);
        cancellationToken.ThrowIfCancellationRequested();
        var afterCopy =
            RecoveryNativeHandleIdProbe.ReadFromExistingHandle(copyAtRecheck);
        cancellationToken.ThrowIfCancellationRequested();
        var afterManifest =
            RecoveryNativeHandleIdProbe.ReadFromExistingHandle(manifestAtRecheck);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = new RecoveryRestartFileIdSnapshot(
            parent, afterParent, copy, afterCopy, manifest, afterManifest);

        return new RecoveryHeldHandleReadbackResult(
            snapshot,
            snapshot.HasStableDistinctFileIds()
                ? RecoveryHeldHandleFinding.StableDistinctHandleIdsOnly
                : RecoveryHeldHandleFinding.IdentityChangedOrAliased);
    }
}

internal enum RecoveryHeldHandleFinding
{
    StableDistinctHandleIdsOnly,
    IdentityChangedOrAliased
}

internal readonly record struct RecoveryHeldHandleReadbackResult(
    RecoveryRestartFileIdSnapshot Snapshot,
    RecoveryHeldHandleFinding Finding)
{
    internal bool HasVerifiedNamesInDirectory => false;
    internal bool HasVerifiedExclusiveWriterLock => false;
    internal bool HasVerifiedCrashDurability => false;
    internal bool CanRestore => false;
    internal bool CanDelete => false;
    internal bool CanPublish => false;
    internal bool ReleaseReady => false;
}
