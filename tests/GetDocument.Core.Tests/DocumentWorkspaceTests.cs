using GetDocument.Core.Workspace;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class DocumentWorkspaceTests
{
    [TestMethod]
    public void LauncherCreatesThreeDifferentDraftTabsInOneWorkspace()
    {
        var workspace = new DocumentWorkspace();
        var text = workspace.CreateDraft(WorkspaceDocumentKind.Text);
        var sheet = workspace.CreateDraft(WorkspaceDocumentKind.Spreadsheet);
        var slides = workspace.CreateDraft(WorkspaceDocumentKind.Presentation);
        Assert.AreEqual(3, workspace.Tabs.Count);
        Assert.AreEqual(WorkspaceDocumentKind.Text, text.Kind);
        Assert.AreEqual(WorkspaceDocumentKind.Spreadsheet, sheet.Kind);
        Assert.AreEqual(WorkspaceDocumentKind.Presentation, slides.Kind);
        Assert.AreEqual(slides.Id, workspace.ActiveId);
        Assert.IsTrue(workspace.Tabs.All(t => t.IsDraft && t.FilePath is null));
    }

    [TestMethod]
    public void OpeningExistingPdfTwiceSelectsSameTab()
    {
        string path = Path.Combine(Path.GetTempPath(), $"get-document-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, [0x25, 0x50, 0x44, 0x46]);
            var workspace = new DocumentWorkspace();
            var first = workspace.OpenExisting(path);
            var second = workspace.OpenExisting(path);
            Assert.AreEqual(first.Id, second.Id);
            Assert.AreEqual(1, workspace.Tabs.Count);
            Assert.AreEqual(WorkspaceDocumentKind.Pdf, first.Kind);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [TestMethod]
    public void ClosingActiveTabSelectsRemainingDocument()
    {
        var workspace = new DocumentWorkspace();
        var first = workspace.CreateDraft(WorkspaceDocumentKind.Text);
        var second = workspace.CreateDraft(WorkspaceDocumentKind.Spreadsheet);
        Assert.IsTrue(workspace.Close(second.Id));
        Assert.AreEqual(first.Id, workspace.ActiveId);
        Assert.IsTrue(workspace.Close(first.Id));
        Assert.IsNull(workspace.ActiveId);
    }

    [TestMethod]
    public void RejectsUnsupportedExtensionsAndInvalidDraftTypes()
    {
        var workspace = new DocumentWorkspace();
        Assert.ThrowsExactly<NotSupportedException>(() => workspace.CreateDraft(WorkspaceDocumentKind.Pdf));
        Assert.ThrowsExactly<NotSupportedException>(() => workspace.OpenExisting("malicious.exe"));
        Assert.AreEqual(0, workspace.Tabs.Count);
    }

    [TestMethod]
    public void FilenameDisplayStripsBidiFormattingWithoutChangingPath()
    {
        Assert.AreEqual("report.pdf", DocumentWorkspace.SafeTitle("report\u202E.pdf"));
        Assert.AreEqual("Документ", DocumentWorkspace.SafeTitle("\u202E\u200F"));
    }
}
