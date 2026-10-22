using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfNativeAnnotationDraftSessionTests
{
    [TestMethod]
    public void InvisibleBidiControlsCannotBeInsertedIntoPdfCommentText()
    {
        var draft = new PdfNativeAnnotationDraft(
            "text", 0, .1, .1, .2, .2, "Приказ" + '\u202e' + "txt");
        Assert.IsFalse(draft.IsValid);
        var session = new PdfNativeAnnotationDraftSession();
        Assert.ThrowsExactly<ArgumentException>(() => session.Add(draft));

        byte[] json = System.Text.Encoding.UTF8.GetBytes(
            "{\"version\":1,\"annotations\":[{\"type\":\"text\",\"page\":1," +
            "\"rect\":[10,10,30,30],\"text\":\"Приказ\\u202etxt\"}]}");
        Assert.ThrowsExactly<InvalidDataException>(() =>
            PdfNativeAnnotationPolicy.ReadRequest(json));
    }

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
