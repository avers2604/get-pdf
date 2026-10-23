using GetDocument.Core.Workspace;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public class DocumentWorkspaceXlsxRevisionTests
{
    [TestMethod]
    public void NewXlsxRevisionRetainsOriginalTabIdentity()
    {
        var workspace = new DocumentWorkspace();
        var draft = workspace.CreateDraft(WorkspaceDocumentKind.Spreadsheet);
        var first = workspace.BindSavedDraft(draft.Id, Path.Combine(Path.GetTempPath(), "source.xlsx"));
        string revised = Path.Combine(Path.GetTempPath(), "new-revision.xlsx");
        try
        {
            File.WriteAllBytes(revised, [1]);
            var updated = workspace.BindNewRevision(first.Id, revised);
            Assert.AreEqual(first.Id, updated.Id);
            Assert.AreEqual(Path.GetFullPath(revised), updated.FilePath);
        }
        finally { File.Delete(revised); }
    }

    [TestMethod]
    public void RejectsMismatchedExtensionForSpreadsheetRevision()
    {
        var workspace = new DocumentWorkspace();
        var tab = workspace.CreateDraft(WorkspaceDocumentKind.Spreadsheet);
        workspace.BindSavedDraft(tab.Id, Path.Combine(Path.GetTempPath(), "table.xlsx"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            workspace.BindNewRevision(tab.Id, Path.Combine(Path.GetTempPath(), "wrong.docx")));
    }
}
