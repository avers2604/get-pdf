using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf;

[TestClass]
public sealed class PdfWorkspacePreferencesTests
{
    [TestMethod]
    public void ToolGroupIdsAreStableAndUnknownSelectionFallsBackToSearch()
    {
        CollectionAssert.AreEqual(new[] { "search", "markup", "pages", "document" },
            Enumerable.Range(0, 4).Select(PdfWorkspaceToolGroups.FromIndex).ToArray());
        Assert.AreEqual(0, PdfWorkspaceToolGroups.ToIndex("other"));
        Assert.AreEqual(0, PdfWorkspaceToolGroups.ToIndex(null));
        Assert.AreEqual("search", PdfWorkspaceToolGroups.FromIndex(99));
        Assert.AreEqual(2, PdfWorkspaceToolGroups.ToIndex("pages"));
    }

    [TestMethod]
    public void MissingOrDamagedPreferencesUseSafeDefault()
    {
        string path = Path.Combine(Path.GetTempPath(), "get-pdf-test-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.AreEqual("search", PdfWorkspacePreferencesStore.LoadFrom(path).ActiveTool);
        try
        {
            File.WriteAllText(path, "{broken");
            Assert.AreEqual("search", PdfWorkspacePreferencesStore.LoadFrom(path).ActiveTool);
            File.WriteAllText(path, "{\"Version\":99,\"ActiveTool\":\"markup\"}");
            Assert.AreEqual("search", PdfWorkspacePreferencesStore.LoadFrom(path).ActiveTool);
            File.WriteAllText(path, "{\"Version\":1,\"ActiveTool\":\"invalid\"}");
            Assert.AreEqual("search", PdfWorkspacePreferencesStore.LoadFrom(path).ActiveTool);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [TestMethod]
    public void SaveAndLoadPersistOnlyApprovedToolGroup()
    {
        string folder = Path.Combine(Path.GetTempPath(), "get-pdf-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "pdf-workspace.json");
        try
        {
            PdfWorkspacePreferencesStore.SaveTo(path, new PdfWorkspacePreferences(1, "pages"));
            Assert.AreEqual("pages", PdfWorkspacePreferencesStore.LoadFrom(path).ActiveTool);
            PdfWorkspacePreferencesStore.SaveTo(path, new PdfWorkspacePreferences(1, "evil-unsupported"));
            Assert.AreEqual("search", PdfWorkspacePreferencesStore.LoadFrom(path).ActiveTool);
            Assert.AreEqual(1, Directory.GetFiles(folder).Length);
            Assert.IsFalse(File.ReadAllText(path).Contains("DeviceId", StringComparison.Ordinal));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void OversizedPreferencesDoNotParseAndCannotSelectUnexpectedMode()
    {
        string path = Path.Combine(Path.GetTempPath(), "get-pdf-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, new string('x', 5000));
            Assert.AreEqual("search", PdfWorkspacePreferencesStore.LoadFrom(path).ActiveTool);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
