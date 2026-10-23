using GetPdf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public class DocumentSessionCompatibilityTests
{
    [TestMethod]
    public void PageSessionPreservesUndoAndSavedStateAfterExtraction()
    {
        var session = new DocumentSession();
        var page = DocumentPageEntry.FromImage("scratch-source");
        session.Append([page]);
        session.MarkSaved(session.Revision);
        session.Duplicate(page.Id);
        Assert.AreEqual(2, session.Pages.Count);
        Assert.IsTrue(session.HasChanges);
        session.Undo();
        Assert.AreEqual(1, session.Pages.Count);
        Assert.IsFalse(session.HasChanges);
        session.Redo();
        Assert.AreEqual(2, session.Pages.Count);
        Assert.IsTrue(session.HasChanges);
    }
    [TestMethod]
    public void SessionRejectsRepeatedPageIdentity()
    {
        var session = new DocumentSession();
        var page = DocumentPageEntry.FromImage("image");
        session.Append([page]);
        Assert.ThrowsExactly<ArgumentException>(() => session.Append([page]));
        Assert.AreEqual(1, session.Pages.Count);
    }
}
