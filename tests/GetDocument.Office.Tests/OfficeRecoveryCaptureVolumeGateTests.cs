using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27bm: synthetic, same-host cooperative capture coordination.
/// These cases do NOT establish Linux/NTFS/SMB kernel-lock correctness.
/// </summary>
[TestClass]
public sealed class OfficeRecoveryCaptureVolumeGateTests
{
    private static readonly Func<string, RecoveryCaptureVolumeObservation> LocalWithSpace =
        _ => new RecoveryCaptureVolumeObservation(
            IsApprovedLocalVolume: true,
            AvailableFreeBytes: RecoveryCaptureVolumeGate.MinimumFreeBytes + 1,
            FileSystemType: "synthetic-local");

    [TestMethod]
    public async Task FirstLeaseBlocksSecondCaptureOnSameRecoveryRoot()
    {
        using var scratch = new Scratch();
        using var first = await RecoveryCaptureVolumeGate.AcquireAsync(
            scratch.Root, LocalWithSpace);
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
        {
            using var _ = await RecoveryCaptureVolumeGate.AcquireAsync(
                scratch.Root, LocalWithSpace);
        });
        Assert.IsTrue(File.Exists(Path.Combine(scratch.Root,
            RecoveryCaptureVolumeGate.LockFileName)));
    }

    [TestMethod]
    public async Task ReleasedLeaseAllowsNextCaptureWithoutRemovingLockFile()
    {
        using var scratch = new Scratch();
        using (var first = await RecoveryCaptureVolumeGate.AcquireAsync(
            scratch.Root, LocalWithSpace)) { }
        using var next = await RecoveryCaptureVolumeGate.AcquireAsync(
            scratch.Root, LocalWithSpace);
        Assert.IsTrue(File.Exists(Path.Combine(scratch.Root,
            RecoveryCaptureVolumeGate.LockFileName)));
    }

    [TestMethod]
    public async Task CanceledAcquisitionNeverCreatesRootOrCallsProbe()
    {
        using var scratch = new Scratch();
        string absent = Path.Combine(scratch.Root, "never-created");
        bool probed = false;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            using var _ = await RecoveryCaptureVolumeGate.AcquireAsync(
                absent, _ =>
                {
                    probed = true;
                    return LocalWithSpace(absent);
                }, cts.Token);
        });
        Assert.IsFalse(probed);
        Assert.IsFalse(Directory.Exists(absent));
    }

    [TestMethod]
    public async Task LowDiskSpaceFailsBeforeCreatingLockOrRunningExporter()
    {
        using var scratch = new Scratch();
        string absent = Path.Combine(scratch.Root, "low-space");
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
        {
            using var _ = await RecoveryCaptureVolumeGate.AcquireAsync(
                absent, _ => new RecoveryCaptureVolumeObservation(
                    true, RecoveryCaptureVolumeGate.MinimumFreeBytes - 1, "ntfs"));
        });
        Assert.IsFalse(Directory.Exists(absent));
    }

    [TestMethod]
    public async Task UnsupportedOrUnknownVolumeFailsClosedWithoutCreatingRoot()
    {
        using var scratch = new Scratch();
        string absent = Path.Combine(scratch.Root, "unapproved");
        await Assert.ThrowsExactlyAsync<PlatformNotSupportedException>(async () =>
        {
            using var _ = await RecoveryCaptureVolumeGate.AcquireAsync(
                absent, _ => new RecoveryCaptureVolumeObservation(
                    false, long.MaxValue, "smb"));
        });
        Assert.IsFalse(Directory.Exists(absent));
    }

    [TestMethod]
    public async Task FreeSpaceIsCheckedAgainWhileHoldingExclusiveLease()
    {
        using var scratch = new Scratch();
        int observations = 0;
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
        {
            using var _ = await RecoveryCaptureVolumeGate.AcquireAsync(
                scratch.Root, _ =>
                {
                    observations++;
                    return new RecoveryCaptureVolumeObservation(
                        true, observations == 1
                            ? RecoveryCaptureVolumeGate.MinimumFreeBytes + 1
                            : RecoveryCaptureVolumeGate.MinimumFreeBytes - 1, "ext4");
                });
        });
        Assert.AreEqual(2, observations);
        using var next = await RecoveryCaptureVolumeGate.AcquireAsync(
            scratch.Root, LocalWithSpace);
        Assert.IsNotNull(next);
    }

    [TestMethod]
    public async Task RejectedLinkedLockFilePreservesExternalTargetBytes()
    {
        using var scratch = new Scratch();
        string protectedFile = Path.Combine(scratch.Root, "external.bin");
        File.WriteAllText(protectedFile, "UNTOUCHED");
        string lockPath = Path.Combine(scratch.Root,
            RecoveryCaptureVolumeGate.LockFileName);
        try { File.CreateSymbolicLink(lockPath, protectedFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or PlatformNotSupportedException) { return; }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            using var _ = await RecoveryCaptureVolumeGate.AcquireAsync(
                scratch.Root, LocalWithSpace);
        });
        Assert.AreEqual("UNTOUCHED", File.ReadAllText(protectedFile));
    }

    [TestMethod]
    public async Task LockIsReleasedOnCaptureFailureWithoutDeletingScratch()
    {
        using var scratch = new Scratch();
        using (var lease = await RecoveryCaptureVolumeGate.AcquireAsync(
            scratch.Root, LocalWithSpace))
        {
            Assert.ThrowsExactly<InvalidOperationException>(
                () => throw new InvalidOperationException("export failed"));
        }
        using var next = await RecoveryCaptureVolumeGate.AcquireAsync(
            scratch.Root, LocalWithSpace);
        Assert.IsTrue(File.Exists(Path.Combine(scratch.Root,
            RecoveryCaptureVolumeGate.LockFileName)));
    }

    private sealed class Scratch : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(),
            "getdoc-gd31j27bm-scratch-" + Guid.NewGuid().ToString("N"));

        internal Scratch() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
