using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27ca block 3: Linux-only diagnostic fsync() of an externally
/// opened directory fd, after/before read-only statx evidence. No name
/// resolution, no creation, deletion or rename. The fd must be opened
/// O_RDONLY|O_DIRECTORY; O_PATH cannot be fsynced and WILL fail closed.
/// Not wired to OfficeRecoveryStore or used for production journal commit.
/// </summary>
internal static class RecoveryHeldLinuxDirectorySyncLab
{
    internal static bool ProductionDirectorySyncEnabled => false;
    internal static bool WindowsDirectorySyncImplemented => false;
    internal static bool SmbDurabilityVerified => false;
    internal static bool CanAuthorizeJournalCommit => false;

    internal static RecoveryHeldDirectorySyncFinding Evaluate(
        RecoveryLinuxParentEvidence before,
        RecoveryLinuxParentEvidence after,
        uint expectedEffectiveUid)
    {
        if (!Eligible(before, expectedEffectiveUid) ||
            !Eligible(after, expectedEffectiveUid))
            return RecoveryHeldDirectorySyncFinding.IneligibleDirectory;
        if (before != after)
            return RecoveryHeldDirectorySyncFinding.ChangedDirectory;
        return RecoveryHeldDirectorySyncFinding.MatchedButUntrusted;
    }

    /// <summary>
    /// Attempts an actual Linux directory fsync on a held fd. This only
    /// confirms success of the syscall (not media persistence, root trust,
    /// SMB server semantics, namespace isolation or File ID/name binding).
    /// Caller is responsible for opening/owning/closing the dir handle.
    /// </summary>
    internal static RecoveryHeldDirectorySyncReceipt SyncExistingDirectoryForLab(
        SafeFileHandle heldDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(heldDirectory);
        if (heldDirectory.IsClosed || heldDirectory.IsInvalid)
            throw new ObjectDisposedException(nameof(heldDirectory));
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() || !BitConverter.IsLittleEndian ||
            RuntimeInformation.ProcessArchitecture is not
                (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException(
                "Нативный fsync(dirfd) этого лабораторного адаптера поддерживает только Linux x64/arm64.");

        bool pinned = false;
        try
        {
            heldDirectory.DangerousAddRef(ref pinned);
            uint uid = GetEffectiveUserId();
            RecoveryLinuxParentEvidence first =
                RecoveryPinnedLinuxParentProbe.ReadExistingDirectoryEvidence(heldDirectory);
            RecoveryPinnedLinuxParentProbe.RequirePrivateOwner(first, uid);
            cancellationToken.ThrowIfCancellationRequested();

            int result;
            try
            {
                result = Fsync(heldDirectory.DangerousGetHandle().ToInt32());
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new PlatformNotSupportedException("libc fsync отсутствует.", ex);
            }
            if (result != 0)
                throw new IOException(
                    "fsync для удерживаемого каталога завершился ошибкой.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));

            // A successful syscall does not stop another writer from
            // modifying the name immediately afterwards.
            RecoveryLinuxParentEvidence later =
                RecoveryPinnedLinuxParentProbe.ReadExistingDirectoryEvidence(heldDirectory);
            if (GetEffectiveUserId() != uid ||
                Evaluate(first, later, uid) !=
                    RecoveryHeldDirectorySyncFinding.MatchedButUntrusted)
                throw new InvalidDataException(
                    "Изменились каталог, его права или эффективный UID во время fsync.");
            cancellationToken.ThrowIfCancellationRequested();
            return new RecoveryHeldDirectorySyncReceipt(
                first.FileId, true,
                RecoveryHeldDirectorySyncFinding.MatchedButUntrusted);
        }
        finally
        {
            if (pinned) heldDirectory.DangerousRelease();
        }
    }

    private static bool Eligible(RecoveryLinuxParentEvidence item, uint uid)
    {
        try
        {
            RecoveryPinnedLinuxParentProbe.RequirePrivateOwner(item, uid);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "fsync", ExactSpelling = true, SetLastError = true)]
    private static extern int Fsync(int fd);

    [DllImport("libc", EntryPoint = "geteuid", ExactSpelling = true)]
    private static extern uint GetEffectiveUserId();
}

internal enum RecoveryHeldDirectorySyncFinding
{
    IneligibleDirectory,
    ChangedDirectory,
    MatchedButUntrusted
}

internal readonly record struct RecoveryHeldDirectorySyncReceipt(
    RecoveryNativeFileId DirectoryId,
    bool DirectorySyncAttempted,
    RecoveryHeldDirectorySyncFinding Finding)
{
    internal bool HasIndependentlyVerifiedDurability => false;
    internal bool CanAuthorizePublication => false;
    internal bool CanAuthorizeDeletion => false;
}
