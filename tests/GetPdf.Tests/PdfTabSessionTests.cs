using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace GetPdf.Tests;

[TestClass]
public sealed class PdfTabSessionTests
{
    [TestMethod]
    public void TabsKeepOrderSelectExistingAndAvoidPathDuplicates()
    {
        var tabs = new PdfTabRegistry();
        string a = Path.Combine(Path.GetTempPath(), "a.pdf");
        string b = Path.Combine(Path.GetTempPath(), "b.pdf");
        var first = tabs.Open(a);
        var second = tabs.Open(b);
        Assert.AreEqual(2, tabs.Items.Count);
        var again = tabs.Open(a.ToUpperInvariant());
        Assert.AreEqual(first.Id, again.Id);
        Assert.AreEqual(2, tabs.Items.Count);
        Assert.AreEqual(first.Id, tabs.ActiveId);
        tabs.Select(second.Id);
        Assert.AreEqual(second.Id, tabs.ActiveId);
        tabs.Close(second.Id);
        Assert.AreEqual(first.Id, tabs.ActiveId);
    }

    [TestMethod]
    public void EmptyTabCanBecomePdfAndSessionIsPersistedLocally()
    {
        var tabs = new PdfTabRegistry();
        var empty = tabs.Open(null);
        string firstPath = Path.Combine(Path.GetTempPath(), "saved.pdf");
        tabs.AssignPath(empty.Id, firstPath);
        var snapshot = tabs.Snapshot();
        Assert.HasCount(1, snapshot.Paths);
        Assert.AreEqual(Path.GetFullPath(firstPath), snapshot.Paths[0]);
        Assert.AreEqual(0, snapshot.ActiveIndex);
    }

    [TestMethod]
    public void SaveAndReadRejectsInvalidPathsAndBoundsEntries()
    {
        string dir = Path.Combine(Path.GetTempPath(), "getpdf-tabtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "tabs.json");
            var state = new PdfTabSnapshot([Path.Combine(dir, "one.pdf")], 0);
            PdfTabSessionStore.SaveTo(file, state);
            CollectionAssert.AreEqual(state.Paths.ToArray(),
                PdfTabSessionStore.LoadFrom(file).Paths.ToArray());
            File.WriteAllText(file, "{broken");
            Assert.HasCount(0, PdfTabSessionStore.LoadFrom(file).Paths);
            Assert.ThrowsExactly<ArgumentException>(() =>
                PdfTabSessionStore.SaveTo(file, new PdfTabSnapshot(["relative.pdf"], 0)));
        }
        finally { Directory.Delete(dir, recursive:true); }
    }

    [TestMethod]
    public void CycleSupportsReverseAndEmptyCases()
    {
        Assert.AreEqual(2, PdfTabNavigation.Cycle(0, 3, -1));
        Assert.AreEqual(0, PdfTabNavigation.Cycle(2, 3, 1));
        Assert.AreEqual(-1, PdfTabNavigation.Cycle(0, 0, 1));
    }

    [TestMethod]
    public void ReattachingTabPreservesOriginalIdentity()
    {
        var a = new PdfTabRegistry();
        var original = a.Open(Path.Combine(Path.GetTempPath(), "sample.pdf"));
        var item = a.Detach(original.Id);
        var b = new PdfTabRegistry();
        b.Attach(item);
        Assert.AreEqual(original.Id, b.Items.Single().Id);
    }

    [TestMethod]
    public void ReorderAndDetachPreserveIdentity()
    {
        var tabs = new PdfTabRegistry();
        var a = tabs.Open("C:\\Documents\\one.pdf");
        var b = tabs.Open("C:\\Documents\\two.pdf");
        tabs.Move(b.Id, 0);
        Assert.AreEqual(b.Id, tabs.Items[0].Id);
        var detached = tabs.Detach(b.Id);
        Assert.AreEqual(b.Id, detached.Id);
        Assert.AreEqual(a.Id, tabs.ActiveId);
        Assert.HasCount(1, tabs.Items);
    }
}
