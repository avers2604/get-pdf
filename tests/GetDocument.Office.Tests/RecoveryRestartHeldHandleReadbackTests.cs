using GetDocument.Office.LibreOffice;
using Microsoft.Win32.SafeHandles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27ca / BLOCK 2. Tests are authored, not executed in this stage.
/// File creation is isolated to disposable test fixtures, never user files.
/// </summary>
[TestClass]
public sealed class RecoveryRestartHeldHandleReadbackTests
{
    private static SafeFileHandle Open(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

    [TestMethod]
    public void SixHeldObjectsWithStableIdsAreDiagnosticOnly()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using var data = new Scratch();
        using var parent1 = Open(data.Parent);
        using var parent2 = Open(data.Parent);
        using var copy1 = Open(data.Copy);
        using var copy2 = Open(data.Copy);
        using var manifest1 = Open(data.Manifest);
        using var manifest2 = Open(data.Manifest);
        var result = RecoveryRestartHeldHandleReadback.Observe(
            parent1, parent2, copy1, copy2, manifest1, manifest2);
        Assert.AreEqual(RecoveryHeldHandleFinding.StableDistinctHandleIdsOnly,
            result.Finding);
        Assert.IsTrue(result.Snapshot.HasStableDistinctFileIds());
        Assert.IsFalse(result.HasVerifiedNamesInDirectory);
        Assert.IsFalse(result.CanRestore);
        Assert.IsFalse(result.CanDelete);
        Assert.IsFalse(result.CanPublish);
    }

    [TestMethod]
    public void ChangedCopyReopenIsRejected()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using var data = new Scratch();
        using var p1 = Open(data.Parent);
        using var p2 = Open(data.Parent);
        using var c1 = Open(data.Copy);
        using var c2 = Open(data.Other);
        using var m1 = Open(data.Manifest);
        using var m2 = Open(data.Manifest);
        var result = RecoveryRestartHeldHandleReadback.Observe(
            p1, p2, c1, c2, m1, m2);
        Assert.AreEqual(RecoveryHeldHandleFinding.IdentityChangedOrAliased,
            result.Finding);
        Assert.IsFalse(result.CanRestore);
    }

    [TestMethod]
    public void CopyAndManifestHardLinkAliasIsRejected()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using var data = new Scratch();
        using var p1 = Open(data.Parent);
        using var p2 = Open(data.Parent);
        using var c1 = Open(data.Copy);
        using var c2 = Open(data.Copy);
        using var m1 = Open(data.Copy);
        using var m2 = Open(data.Copy);
        var result = RecoveryRestartHeldHandleReadback.Observe(
            p1, p2, c1, c2, m1, m2);
        Assert.AreEqual(RecoveryHeldHandleFinding.IdentityChangedOrAliased,
            result.Finding);
    }

    [TestMethod]
    public void CancellationPreventsAnyNativeProbe()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            RecoveryRestartHeldHandleReadback.Observe(
                null!, null!, null!, null!, null!, null!, cancel.Token));
    }

    [TestMethod]
    public void DisposedAndNullHandlesFailClosed()
    {
        using var data = new Scratch();
        using var closed = Open(data.Parent);
        closed.Dispose();
        using var live = Open(data.Copy);
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            RecoveryRestartHeldHandleReadback.Observe(
                closed, live, live, live, live, live));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            RecoveryRestartHeldHandleReadback.Observe(
                null!, live, live, live, live, live));
    }

    [TestMethod]
    public void ReadbackDoesNotModifyAnyFixtureContent()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using var data = new Scratch();
        using var p1 = Open(data.Parent);
        using var p2 = Open(data.Parent);
        using var c1 = Open(data.Copy);
        using var c2 = Open(data.Copy);
        using var m1 = Open(data.Manifest);
        using var m2 = Open(data.Manifest);
        _ = RecoveryRestartHeldHandleReadback.Observe(
            p1, p2, c1, c2, m1, m2);
        Assert.AreEqual("PARENT", File.ReadAllText(data.Parent));
        Assert.AreEqual("COPY", File.ReadAllText(data.Copy));
        Assert.AreEqual("MANIFEST", File.ReadAllText(data.Manifest));
    }

    private sealed class Scratch : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(),
            "getdoc-ca-" + Guid.NewGuid().ToString("N"));
        internal string Parent => Path.Combine(root, "parent.txt");
        internal string Copy => Path.Combine(root, "copy.txt");
        internal string Manifest => Path.Combine(root, "manifest.txt");
        internal string Other => Path.Combine(root, "other.txt");

        internal Scratch()
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Parent, "PARENT");
            File.WriteAllText(Copy, "COPY");
            File.WriteAllText(Manifest, "MANIFEST");
            File.WriteAllText(Other, "OTHER");
        }

        public void Dispose()
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
