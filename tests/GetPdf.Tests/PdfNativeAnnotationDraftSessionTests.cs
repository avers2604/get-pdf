using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfNativeAnnotationDraftSessionTests
{
    [TestMethod]
    public void SnapshotContainsAllPagesAndCannotMutateDraftSession()
    {
        var drafts = new PdfNativeAnnotationDraftSession();
        drafts.Add(new PdfNativeAnnotationDraft("text", 0, .1, .1, .2, .2, "Первый"));
        drafts.Add(new PdfNativeAnnotationDraft("text", 1, .2, .2, .3, .3, "Второй"));
        var snapshot = drafts.Snapshot();
        Assert.AreEqual(2, snapshot.Count);
        Assert.AreEqual("Первый", snapshot[0].Text);
        Assert.AreEqual(1, snapshot[1].PageIndex);
        drafts.Clear();
        Assert.AreEqual(2, snapshot.Count);
        Assert.AreEqual(0, drafts.Count);
    }
}
