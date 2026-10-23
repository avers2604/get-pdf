using GetDocument.Core.Files;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class RootedFileBrowserTests
{
    [TestMethod]
    public void ListsOnlyImmediateChildrenAndMarksUnsupportedFiles()
    {
        using var folder = Scratch.Create();
        Directory.CreateDirectory(Path.Combine(folder.Path, "Отдел"));
        File.WriteAllText(Path.Combine(folder.Path, "Акт.pdf"), "test");
        File.WriteAllText(Path.Combine(folder.Path, "danger.exe"), "test");
        var browser = new RootedFileBrowser(folder.Path);
        var snapshot = browser.Scan();
        Assert.AreEqual(3, snapshot.Entries.Count);
        Assert.IsTrue(snapshot.Entries.Any(e => e.IsDirectory && e.Name == "Отдел"));
        Assert.IsTrue(snapshot.Entries.Any(e => e.Name == "Акт.pdf" && e.CanOpen));
        Assert.IsTrue(snapshot.Entries.Any(e => e.Name == "danger.exe" && !e.CanOpen));
        Assert.IsFalse(snapshot.IsTruncated);
    }

    [TestMethod]
    public void DescendsAndReturnsToRootWithoutEscaping()
    {
        using var folder = Scratch.Create();
        string nested = Directory.CreateDirectory(Path.Combine(folder.Path, "nested")).FullName;
        var browser = new RootedFileBrowser(folder.Path);
        browser.NavigateTo(nested);
        Assert.AreEqual(nested, browser.CurrentPath);
        Assert.IsTrue(browser.GoUp());
        Assert.AreEqual(folder.Path, browser.CurrentPath);
        Assert.IsFalse(browser.GoUp());
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            browser.NavigateTo(Path.GetDirectoryName(folder.Path)!));
    }

    [TestMethod]
    public void DoesNotTraverseLinksOutsideSelectedRoot()
    {
        if (!OperatingSystem.IsLinux()) return; // Linux/Samba path acceptance
        using var root = Scratch.Create();
        using var elsewhere = Scratch.Create();
        string link = Path.Combine(root.Path, "external");
        Directory.CreateSymbolicLink(link, elsewhere.Path);
        var browser = new RootedFileBrowser(root.Path);
        Assert.IsFalse(browser.Scan().Entries.Any(e => e.Name == "external"));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => browser.NavigateTo(link));
    }

    [TestMethod]
    public void ScanIsBoundedForLargeSmbDirectories()
    {
        using var folder = Scratch.Create();
        for (int i = 0; i < 20; i++)
            File.WriteAllText(Path.Combine(folder.Path, $"file-{i:00}.pdf"), "PDF");
        var snapshot = new RootedFileBrowser(folder.Path).Scan(maxEntries: 5);
        Assert.AreEqual(5, snapshot.Entries.Count);
        Assert.IsTrue(snapshot.IsTruncated);
    }

    [TestMethod]
    public void RechecksFileExistenceAndTypeImmediatelyBeforeOpening()
    {
        using var folder = Scratch.Create();
        string pdf = Path.Combine(folder.Path, "test.pdf");
        string exe = Path.Combine(folder.Path, "bad.exe");
        File.WriteAllText(pdf, "pdf");
        File.WriteAllText(exe, "exe");
        var browser = new RootedFileBrowser(folder.Path);
        Assert.AreEqual(pdf, browser.ResolveOpenableFile(pdf));
        Assert.ThrowsExactly<NotSupportedException>(() => browser.ResolveOpenableFile(exe));
        File.Delete(pdf);
        Assert.ThrowsExactly<FileNotFoundException>(() => browser.ResolveOpenableFile(pdf));
    }

    [TestMethod]
    public void DirectoryNavigationDoesNotAllowLexicalTraversal()
    {
        using var root = Scratch.Create();
        using var outside = Scratch.Create();
        var browser = new RootedFileBrowser(root.Path);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => browser.NavigateTo(outside.Path));
    }

    private sealed class Scratch : IDisposable
    {
        public string Path { get; }
        private Scratch(string path) { Path = path; Directory.CreateDirectory(path); }
        public static Scratch Create() => new(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "get-files-" + Guid.NewGuid().ToString("N")));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
