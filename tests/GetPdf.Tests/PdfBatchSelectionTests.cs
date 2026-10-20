using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf;

[TestClass]
public sealed class PdfBatchSelectionTests
{
    [TestMethod]
    public void ReordersAndDuplicatesPrintReferencesWithoutRepeatedRasterImport()
    {
        var plan = new PdfPagePlan(8);
        plan.Move(7, 0);
        plan.Duplicate(0);
        plan.SetStamp(1, "КОПИЯ");
        var selected = PdfBatchSelection.Build([plan.Pages[0], plan.Pages[1], plan.Pages[2]]);
        CollectionAssert.AreEqual(new[] { 7, 0 }, selected.SourcePages.ToArray());
        CollectionAssert.AreEqual(new[] { 0, 0, 1 },
            selected.PreparedPages.Select(page => page.SourcePage).ToArray());
        Assert.AreEqual("КОПИЯ", selected.PreparedPages[1].Stamp);
    }

    [TestMethod]
    public void PureSubsetKeepsOnlySelectedSourcePages()
    {
        var plan = new PdfPagePlan(500);
        var selected = PdfBatchSelection.Build([plan.Pages[0], plan.Pages[5], plan.Pages[10]]);
        CollectionAssert.AreEqual(new[] { 0, 5, 10 }, selected.SourcePages.ToArray());
        Assert.AreEqual(3, selected.PreparedPages.Count);
    }

    [TestMethod]
    public void EmptyOrInvalidPageIsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            PdfBatchSelection.Build([]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PdfBatchSelection.Build([new PdfPageReference(-1, 0)]));
    }
}
