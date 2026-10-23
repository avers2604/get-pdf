using GetDocument.Core.Workspace;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class DocumentWorkspaceSaveTests
{
    [DataTestMethod]
    [DataRow(WorkspaceDocumentKind.Text, ".docx")]
    [DataRow(WorkspaceDocumentKind.Spreadsheet, ".xlsx")]
    [DataRow(WorkspaceDocumentKind.Presentation, ".pptx")]
    public void SavingDraftRetainsTabIdentityAndBecomesSaved(WorkspaceDocumentKind kind, string extension)
    {
        var workspace = new DocumentWorkspace();
        var draft = workspace.CreateDraft(kind);
        string path = Path.Combine(Path.GetTempPath(), "sample" + extension);
        var saved = workspace.BindSavedDraft(draft.Id, path);
        Assert.AreEqual(draft.Id, saved.Id);
        Assert.AreEqual(kind, saved.Kind);
        Assert.IsFalse(saved.IsDraft);
        Assert.AreEqual(Path.GetFullPath(path), saved.FilePath);
        Assert.AreEqual(1, workspace.Tabs.Count);
        Assert.AreEqual(saved.Id, workspace.ActiveId);
    }

    [TestMethod]
    public void RejectsWrongExtensionWithoutChangingDraft()
    {
        var workspace = new DocumentWorkspace();
        var draft = workspace.CreateDraft(WorkspaceDocumentKind.Text);
        Assert.ThrowsExactly<ArgumentException>(() => workspace.BindSavedDraft(draft.Id, "/tmp/other.xlsx"));
        Assert.IsTrue(workspace.Tabs[0].IsDraft);
        Assert.IsNull(workspace.Tabs[0].FilePath);
    }

    [TestMethod]
    public void CannotBindPathAlreadyUsedByAnotherTab()
    {
        var workspace = new DocumentWorkspace();
        var first = workspace.CreateDraft(WorkspaceDocumentKind.Text);
        var second = workspace.CreateDraft(WorkspaceDocumentKind.Text);
        string path = Path.Combine(Path.GetTempPath(), "duplicate.docx");
        workspace.BindSavedDraft(first.Id, path);
        Assert.ThrowsExactly<InvalidOperationException>(() => workspace.BindSavedDraft(second.Id, path));
        Assert.IsTrue(workspace.Tabs[1].IsDraft);
    }
}
