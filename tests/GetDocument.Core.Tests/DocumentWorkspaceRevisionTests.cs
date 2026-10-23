using GetDocument.Core.Workspace;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class DocumentWorkspaceRevisionTests
{
    [TestMethod]
    public void SavedDocumentKeepsTabIdentityWhenRevisionPublished()
    {
        using var test = new Fixture();
        var workspace = new DocumentWorkspace();
        var draft = workspace.CreateDraft(WorkspaceDocumentKind.Text);
        workspace.BindSavedDraft(draft.Id, test.Original);
        File.WriteAllText(test.Revision, "OOXML fixture placeholder");
        var result = workspace.BindNewRevision(draft.Id, test.Revision);
        Assert.AreEqual(draft.Id, result.Id);
        Assert.AreEqual(Path.GetFullPath(test.Revision), result.FilePath);
        Assert.AreEqual(1, workspace.Tabs.Count);
    }

    [TestMethod]
    public void PreventsUsingOriginalAsRevisionDestination()
    {
        using var test = new Fixture();
        var workspace = new DocumentWorkspace();
        var draft = workspace.CreateDraft(WorkspaceDocumentKind.Text);
        workspace.BindSavedDraft(draft.Id, test.Original);
        Assert.ThrowsExactly<IOException>(() => workspace.BindNewRevision(draft.Id, test.Original));
        Assert.AreEqual(Path.GetFullPath(test.Original), workspace.Tabs.Single().FilePath);
    }

    [TestMethod]
    public void CannotRebindToUnsavedOrConflictingPath()
    {
        using var test = new Fixture();
        var workspace = new DocumentWorkspace();
        var a = workspace.CreateDraft(WorkspaceDocumentKind.Text);
        Assert.ThrowsExactly<InvalidOperationException>(() => workspace.BindNewRevision(a.Id, test.Revision));
        workspace.BindSavedDraft(a.Id, test.Original);
        var b = workspace.CreateDraft(WorkspaceDocumentKind.Text);
        workspace.BindSavedDraft(b.Id, test.Revision);
        Assert.ThrowsExactly<InvalidOperationException>(() => workspace.BindNewRevision(a.Id, test.Revision));
        Assert.ThrowsExactly<ArgumentException>(() => workspace.BindNewRevision(a.Id, test.Revision + ".xls"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "getdoc-core-" + Guid.NewGuid().ToString("N"));
        public string Original => Path.Combine(_root, "original.docx");
        public string Revision => Path.Combine(_root, "revision.docx");
        public Fixture() => Directory.CreateDirectory(_root);
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
