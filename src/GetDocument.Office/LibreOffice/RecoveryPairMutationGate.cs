using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27bv: strictly denial-only evidence for future native file-pair
/// publication/deletion. IDs from held handles are not a name-bound operation.
/// Same-directory FileInfo, File.Delete and File.Move cannot resist a hostile
/// rename between a check and the mutation.
/// </summary>
internal static class RecoveryPairMutationGate
{
    internal static bool CanDeleteByPath => false;
    internal static bool CanPublishByPath => false;

    internal static RecoveryPairMutationDecision Evaluate(
        RecoveryRecoveryPairIdentityEvidence evidence)
    {
        RecoveryNativeFileId parent = evidence.Parent;
        RecoveryNativeFileId copy = evidence.Copy;
        RecoveryNativeFileId manifest = evidence.Manifest;
        if (!parent.IsComplete || !copy.IsComplete || !manifest.IsComplete ||
            !evidence.CopyAtRecheck.IsComplete || !evidence.ManifestAtRecheck.IsComplete ||
            parent.Platform != copy.Platform ||
            parent.Platform != manifest.Platform ||
            parent.Platform != evidence.CopyAtRecheck.Platform ||
            parent.Platform != evidence.ManifestAtRecheck.Platform)
            return RecoveryPairMutationDecision.IncompleteNativeIdentity;

        if (copy != evidence.CopyAtRecheck ||
            manifest != evidence.ManifestAtRecheck)
            return RecoveryPairMutationDecision.ReplacedOrChanged;

        if (parent == copy || parent == manifest || copy == manifest)
            return RecoveryPairMutationDecision.AliasedPair;

        if (parent.DeviceOrVolume != copy.DeviceOrVolume ||
            parent.DeviceOrVolume != manifest.DeviceOrVolume)
            return RecoveryPairMutationDecision.CrossVolume;

        if (!evidence.ParentHandleHeld || !evidence.NamesOpenedRelativeToParent ||
            !evidence.NoFollowEnforced || !evidence.WriterIsolationConfirmed ||
            !evidence.LockHeld)
            return RecoveryPairMutationDecision.MissingExclusiveOwnership;

        // Even idealized, self-reported evidence is NOT an OS transaction.
        // No unlinkat()/renameat2()/SetFileInformationByHandle() capability
        // is provided until an independently audited native backend holds
        // the directory and required lock over the mutation itself.
        return RecoveryPairMutationDecision.NeedsNativeBoundOperation;
    }

    internal static bool CanAuthorizeNativeMutation(
        RecoveryRecoveryPairIdentityEvidence evidence)
    {
        _ = Evaluate(evidence);
        return false;
    }

    /// <summary>
    /// Pure read-only observation from three caller-owned open file handles.
    /// Does not open paths, follow links, delete or write anything. The
    /// identity can remain stable even when a directory ENTRY is renamed;
    /// hence this evidence still cannot approve any destructive operation.
    /// </summary>
    internal static RecoveryRecoveryPairIdentityEvidence ObserveHeldPair(
        SafeFileHandle heldParent, SafeFileHandle heldCopy,
        SafeFileHandle heldManifest, CancellationToken cancellationToken = default)
    {
        EnsureOpen(heldParent);
        EnsureOpen(heldCopy);
        EnsureOpen(heldManifest);
        cancellationToken.ThrowIfCancellationRequested();
        RecoveryNativeFileId parent =
            RecoveryNativeHandleIdProbe.ReadFromExistingHandle(heldParent);
        RecoveryNativeFileId copy =
            RecoveryNativeHandleIdProbe.ReadFromExistingHandle(heldCopy);
        RecoveryNativeFileId manifest =
            RecoveryNativeHandleIdProbe.ReadFromExistingHandle(heldManifest);
        cancellationToken.ThrowIfCancellationRequested();
        RecoveryNativeFileId afterCopy =
            RecoveryNativeHandleIdProbe.ReadFromExistingHandle(heldCopy);
        RecoveryNativeFileId afterManifest =
            RecoveryNativeHandleIdProbe.ReadFromExistingHandle(heldManifest);

        // This method did NOT prove the parent handle is trusted, an entry
        // name refers to the held copy/manifest, or any exclusive writer lock.
        // These fields therefore remain false without any caller override.
        return new RecoveryRecoveryPairIdentityEvidence(parent, copy, manifest,
            afterCopy, afterManifest, ParentHandleHeld: false,
            NamesOpenedRelativeToParent: false, NoFollowEnforced: false,
            WriterIsolationConfirmed: false, LockHeld: false);
    }

    private static void EnsureOpen(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsClosed || handle.IsInvalid)
            throw new ObjectDisposedException(nameof(handle));
    }
}

internal enum RecoveryPairMutationDecision
{
    IncompleteNativeIdentity,
    ReplacedOrChanged,
    AliasedPair,
    CrossVolume,
    MissingExclusiveOwnership,
    NeedsNativeBoundOperation
}

/// <summary>
/// Conceptual evidence contract, not a capability/token. The booleans are
/// synthetic claims and cannot be used to promote identity to write access.
/// </summary>
internal readonly record struct RecoveryRecoveryPairIdentityEvidence(
    RecoveryNativeFileId Parent,
    RecoveryNativeFileId Copy,
    RecoveryNativeFileId Manifest,
    RecoveryNativeFileId CopyAtRecheck,
    RecoveryNativeFileId ManifestAtRecheck,
    bool ParentHandleHeld,
    bool NamesOpenedRelativeToParent,
    bool NoFollowEnforced,
    bool WriterIsolationConfirmed,
    bool LockHeld);
