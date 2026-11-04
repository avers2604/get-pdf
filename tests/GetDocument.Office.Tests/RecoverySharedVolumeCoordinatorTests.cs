using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27bo: isolated synthetic same-process tests. They deliberately
/// DO NOT claim to test cross-process byte-range locking or crash recovery.
/// Those are distinct Windows/Astra manual gates.
/// </summary>
[TestClass]
public sealed class RecoverySharedVolumeCoordinatorTests
{
    [TestMethod]
    public void SameVolumeKeyAcrossDifferentRecoveryRootsDerivesSameLockName()
    {
        using var scratch = new Scratch();
        string first = RecoverySharedVolumeCoordinator.GetLockPathForPlanning(
            scratch.CoordinatorRoot, "mounted-volume-one");
        string second = RecoverySharedVolumeCoordinator.GetLockPathForPlanning(
            scratch.CoordinatorRoot, "mounted-volume-one");

        Assert.AreEqual(first, second);
        Assert.IsTrue(Path.GetFileName(first).StartsWith(
            ".getdoc-volume-", StringComparison.Ordinal));
        Assert.IsTrue(Path.GetFileName(first).EndsWith(".lock", StringComparison.Ordinal));
        Assert.IsFalse(first.Contains("mounted-volume-one", StringComparison.Ordinal));
        Assert.IsFalse(Directory.Exists(scratch.CoordinatorRoot));
    }

    [TestMethod]
    public void DifferentVolumeKeysCannotAliasTheSameCoordinatorFile()
    {
        using var scratch = new Scratch();
        string first = RecoverySharedVolumeCoordinator.GetLockPathForPlanning(
            scratch.CoordinatorRoot, "mounted-volume-one");
        string second = RecoverySharedVolumeCoordinator.GetLockPathForPlanning(
            scratch.CoordinatorRoot, "mounted-volume-two");
        Assert.AreNotEqual(first, second);
    }

    [TestMethod]
    public async Task HeldCoordinatorIsPersistentAcrossCloseAndReopen()
    {
        using var scratch = new Scratch();
        string path = RecoverySharedVolumeCoordinator.GetLockPathForPlanning(
            scratch.CoordinatorRoot, "test-volume");

        using (var held = await RecoverySharedVolumeCoordinator.AcquireAsync(
            "test-volume", scratch.CoordinatorRoot))
        {
            Assert.IsTrue(File.Exists(path));
            Assert.AreEqual(0, new FileInfo(path).Length);
            held.Dispose();
        }

        Assert.IsTrue(File.Exists(path),
            "A stale lock name must be retained; it is not an orphan to reap.");
        using var reopened = await RecoverySharedVolumeCoordinator.AcquireAsync(
            "test-volume", scratch.CoordinatorRoot);
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task PreCancelledAcquireNeverCreatesCoordinatorDirectory()
    {
        using var scratch = new Scratch();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            using var ignored = await RecoverySharedVolumeCoordinator.AcquireAsync(
                "test-volume", scratch.CoordinatorRoot, cancelled.Token);
        });

        Assert.IsFalse(Directory.Exists(scratch.CoordinatorRoot));
    }

    [TestMethod]
    public async Task ExistingSymlinkedCoordinatorLockIsRejectedWithoutModifyingTarget()
    {
        using var scratch = new Scratch();
        Directory.CreateDirectory(scratch.CoordinatorRoot);
        string target = Path.Combine(scratch.Root, "protected.txt");
        File.WriteAllText(target, "KEEP_UNCHANGED");
        string link = RecoverySharedVolumeCoordinator.GetLockPathForPlanning(
            scratch.CoordinatorRoot, "test-volume");
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or PlatformNotSupportedException) { return; }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            using var ignored = await RecoverySharedVolumeCoordinator.AcquireAsync(
                "test-volume", scratch.CoordinatorRoot);
        });
        Assert.AreEqual("KEEP_UNCHANGED", File.ReadAllText(target));
    }

    [TestMethod]
    public async Task ExistingNonemptyCoordinatorLockIsRejectedAndRetained()
    {
        using var scratch = new Scratch();
        Directory.CreateDirectory(scratch.CoordinatorRoot);
        string path = RecoverySharedVolumeCoordinator.GetLockPathForPlanning(
            scratch.CoordinatorRoot, "test-volume");
        File.WriteAllText(path, "NOT_OUR_LOCK_FILE");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            using var ignored = await RecoverySharedVolumeCoordinator.AcquireAsync(
                "test-volume", scratch.CoordinatorRoot);
        });
        Assert.AreEqual("NOT_OUR_LOCK_FILE", File.ReadAllText(path));
    }

    [TestMethod]
    public void EmptyOrInvalidVolumeIdentityCannotCreateCoordinatorPath()
    {
        using var scratch = new Scratch();
        foreach (string identity in new[] { "", " ", new string('x', 2049) })
            Assert.ThrowsExactly<ArgumentException>(() =>
                RecoverySharedVolumeCoordinator.GetLockPathForPlanning(
                    scratch.CoordinatorRoot, identity));
        Assert.IsFalse(Directory.Exists(scratch.CoordinatorRoot));
    }

    [TestMethod]
    public async Task SymlinkedCoordinatorDirectoryIsRejectedBeforeLockCreation()
    {
        using var scratch = new Scratch();
        string real = Path.Combine(scratch.Root, "real");
        Directory.CreateDirectory(real);
        try { Directory.CreateSymbolicLink(scratch.CoordinatorRoot, real); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or PlatformNotSupportedException) { return; }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            using var ignored = await RecoverySharedVolumeCoordinator.AcquireAsync(
                "test-volume", scratch.CoordinatorRoot);
        });
        Assert.AreEqual(0, Directory.GetFileSystemEntries(real).Length);
    }

    [TestMethod]
    public async Task NewLinuxCoordinatorDirectoryUsesPrivateOwnerOnlyMode()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scratch = new Scratch();
        using var held = await RecoverySharedVolumeCoordinator.AcquireAsync(
            "synthetic-private-volume", scratch.CoordinatorRoot);
        UnixFileMode mode = Directory.GetUnixFileMode(scratch.CoordinatorRoot);
        Assert.IsTrue((mode & (UnixFileMode.UserRead |
                               UnixFileMode.UserWrite |
                               UnixFileMode.UserExecute)) ==
                       (UnixFileMode.UserRead |
                        UnixFileMode.UserWrite |
                        UnixFileMode.UserExecute));
        Assert.AreEqual(UnixFileMode.None,
            mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                    UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                    UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));
    }

    [TestMethod]
    public async Task ExistingWorldReadableLinuxCoordinatorIsDeniedNotChmodded()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scratch = new Scratch();
        Directory.CreateDirectory(scratch.CoordinatorRoot);
        UnixFileMode original = UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.UserExecute | UnixFileMode.GroupRead;
        File.SetUnixFileMode(scratch.CoordinatorRoot, original);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            using var ignored = await RecoverySharedVolumeCoordinator.AcquireAsync(
                "synthetic-private-volume", scratch.CoordinatorRoot);
        });
        Assert.AreEqual(original, Directory.GetUnixFileMode(scratch.CoordinatorRoot));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(scratch.CoordinatorRoot).Length);
    }

    [TestMethod]
    public async Task ExistingGroupWritableLinuxCoordinatorFailsBeforeLockCreation()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scratch = new Scratch();
        Directory.CreateDirectory(scratch.CoordinatorRoot);
        UnixFileMode original = UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.UserExecute | UnixFileMode.GroupWrite |
            UnixFileMode.GroupExecute;
        File.SetUnixFileMode(scratch.CoordinatorRoot, original);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            using var ignored = await RecoverySharedVolumeCoordinator.AcquireAsync(
                "synthetic-private-volume", scratch.CoordinatorRoot);
        });
        Assert.AreEqual(original, Directory.GetUnixFileMode(scratch.CoordinatorRoot));
    }

    private sealed class Scratch : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(),
            "getdoc-gd31j27bo-coordinator-" + Guid.NewGuid().ToString("N"));
        internal string CoordinatorRoot => Path.Combine(Root, "coordinator");
        internal Scratch() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
