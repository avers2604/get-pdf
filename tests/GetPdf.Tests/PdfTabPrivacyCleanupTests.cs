using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace GetPdf.Tests;

[TestClass]
public sealed class PdfTabPrivacyCleanupTests
{
    [TestMethod]
    public void DisablingSessionRestorationCanRemoveOldPathMetadata()
    {
        string folder = Path.Combine(Path.GetTempPath(), "getpdf-privacy-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string destination = Path.Combine(folder, "tabs.json");
            PdfTabSessionStore.SaveTo(destination,
                new PdfTabSnapshot([Path.Combine(folder, "doc.pdf")], 0));
            Assert.IsTrue(File.Exists(destination));
            PdfTabSessionStore.ClearFrom(destination);
            Assert.IsFalse(File.Exists(destination));
            Assert.HasCount(0, PdfTabSessionStore.LoadFrom(destination).Paths);
        }
        finally { Directory.Delete(folder, recursive:true); }
    }

    [TestMethod]
    public void ActiveTabIndexSurvivesPureRegistrySnapshot()
    {
        var tabs = new PdfTabRegistry();
        var a = tabs.Open(Path.Combine(Path.GetTempPath(), "a.pdf"));
        var b = tabs.Open(Path.Combine(Path.GetTempPath(), "b.pdf"));
        tabs.Select(b.Id);
        Assert.AreEqual(1, tabs.Snapshot().ActiveIndex);
        tabs.Select(a.Id);
        Assert.AreEqual(0, tabs.Snapshot().ActiveIndex);
    }
}
