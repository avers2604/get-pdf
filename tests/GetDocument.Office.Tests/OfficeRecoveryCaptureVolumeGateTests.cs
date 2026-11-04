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
            using var rejectedOrSecond = await RecoveryCaptureVolumeGate.AcquireAsync(
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
            using var rejectedOrSecond = await RecoveryCaptureVolumeGate.AcquireAsync(
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
            using var rejectedOrSecond = await RecoveryCaptureVolumeGate.AcquireAsync(
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
            using var rejectedOrSecond = await RecoveryCaptureVolumeGate.AcquireAsync(
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
            using var rejectedOrSecond = await RecoveryCaptureVolumeGate.AcquireAsync(
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
            using var rejectedOrSecond = await RecoveryCaptureVolumeGate.AcquireAsync(
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

    [TestMethod]
    public async Task SeparateStoresSharingRootRejectOverlappingExportBeforeItRuns()
    {
        using var scratch = new Scratch();
        string source = Path.Combine(scratch.Root, "draft.docx");
        // A real OOXML fixture is unnecessary: the first exporter deliberately
        // stays blocked; the second must be rejected before its exporter runs.
        File.WriteAllText(source, "SOURCE_NOT_MODIFIED");
        var firstStore = new OfficeRecoveryStore(scratch.Root, LocalWithSpace);
        var secondStore = new OfficeRecoveryStore(scratch.Root, LocalWithSpace);
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<OfficeRecoveryCopy> first = firstStore.CaptureAsync(source, async _ =>
        {
            entered.TrySetResult(true);
            await release.Task;
            throw new IOException("synthetic stopped export");
        });
        await entered.Task;
        bool secondCalled = false;
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await secondStore.CaptureAsync(source, _ =>
            {
                secondCalled = true;
                return Task.CompletedTask;
            }));

        Assert.IsFalse(secondCalled);
        release.TrySetResult(true);
        await Assert.ThrowsExactlyAsync<IOException>(async () => await first);
        Assert.AreEqual("SOURCE_NOT_MODIFIED", File.ReadAllText(source));
        Assert.IsTrue(File.Exists(Path.Combine(scratch.Root,
            RecoveryCaptureVolumeGate.LockFileName)));
    }

    [TestMethod]
    public async Task LowSpaceStoreRefusesExporterAndPreservesSourceWithoutRecoveryFolder()
    {
        using var scratch = new Scratch();
        string source = Path.Combine(scratch.Root, "draft.docx");
        File.WriteAllText(source, "KEEP_SOURCE");
        var store = new OfficeRecoveryStore(scratch.Root, _ =>
            new RecoveryCaptureVolumeObservation(true,
                RecoveryCaptureVolumeGate.MinimumFreeBytes - 1, "synthetic"));
        bool invoked = false;
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await store.CaptureAsync(source, _ =>
            {
                invoked = true;
                return Task.CompletedTask;
            }));
        Assert.IsFalse(invoked);
        Assert.AreEqual("KEEP_SOURCE", File.ReadAllText(source));
        Assert.IsFalse(File.Exists(Path.Combine(scratch.Root,
            RecoveryCaptureVolumeGate.LockFileName)));
    }

    [TestMethod]
    public async Task TwoDistinctRecoveryRootsOnSameVolumeCannotOverlapInOneProcess()
    {
        using var scratch = new Scratch();
        string firstRoot = Path.Combine(scratch.Root, "first-root");
        string secondRoot = Path.Combine(scratch.Root, "second-root");
        Func<string, RecoveryCaptureVolumeObservation> sameVolume = _ =>
            new RecoveryCaptureVolumeObservation(true,
                RecoveryCaptureVolumeGate.MinimumFreeBytes + 1, "synthetic",
                VolumeKey: "volume-one");

        using var first = await RecoveryCaptureVolumeGate.AcquireAsync(
            firstRoot, sameVolume);
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
        {
            using var rejected = await RecoveryCaptureVolumeGate.AcquireAsync(
                secondRoot, sameVolume);
        });
        Assert.IsFalse(Directory.Exists(secondRoot),
            "A rejected second root must not be created or modified.");
    }

    [TestMethod]
    public async Task IndependentSyntheticVolumeKeysDoNotShareLocalGate()
    {
        using var scratch = new Scratch();
        string firstRoot = Path.Combine(scratch.Root, "first-other");
        string secondRoot = Path.Combine(scratch.Root, "second-other");
        Func<string, RecoveryCaptureVolumeObservation> firstVolume = _ =>
            new RecoveryCaptureVolumeObservation(true,
                RecoveryCaptureVolumeGate.MinimumFreeBytes + 1, "synthetic",
                VolumeKey: "volume-one");
        Func<string, RecoveryCaptureVolumeObservation> secondVolume = _ =>
            new RecoveryCaptureVolumeObservation(true,
                RecoveryCaptureVolumeGate.MinimumFreeBytes + 1, "synthetic",
                VolumeKey: "volume-two");

        using var first = await RecoveryCaptureVolumeGate.AcquireAsync(
            firstRoot, firstVolume);
        using var second = await RecoveryCaptureVolumeGate.AcquireAsync(
            secondRoot, secondVolume);
        Assert.IsTrue(File.Exists(Path.Combine(firstRoot,
            RecoveryCaptureVolumeGate.LockFileName)));
        Assert.IsTrue(File.Exists(Path.Combine(secondRoot,
            RecoveryCaptureVolumeGate.LockFileName)));
    }

    [TestMethod]
    public async Task VolumeChangedBetweenProbesFailsClosedAndReleasesGate()
    {
        using var scratch = new Scratch();
        string targetRoot = Path.Combine(scratch.Root, "switched-volume");
        int reads = 0;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            using var rejected = await RecoveryCaptureVolumeGate.AcquireAsync(
                targetRoot, _ => new RecoveryCaptureVolumeObservation(
                    true, RecoveryCaptureVolumeGate.MinimumFreeBytes + 1,
                    "synthetic", ++reads == 1 ? "volume-one" : "volume-two"));
        });
        Assert.AreEqual(2, reads);

        using var accepted = await RecoveryCaptureVolumeGate.AcquireAsync(
            targetRoot, _ => new RecoveryCaptureVolumeObservation(
                true, RecoveryCaptureVolumeGate.MinimumFreeBytes + 1,
                "synthetic", VolumeKey: "volume-one"));
        Assert.IsTrue(File.Exists(Path.Combine(targetRoot,
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
