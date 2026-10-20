using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf;

[TestClass]
public sealed class PdfPageRangeTests
{
    [TestMethod]
    public void RangeSelectsVisiblePagesAfterReordering()
    {
        var plan = new PdfPagePlan(6);
        plan.Move(5, 0);
        var selected = PdfPageRange.Parse("1-2, 5", plan.Pages);
        CollectionAssert.AreEqual(new[] { 5, 0, 1, 2, 3, 4 },
            plan.Pages.Select(p => p.SourcePage).ToArray());
        CollectionAssert.AreEqual(new[] { 5, 0, 3 },
            selected.Select(p => p.SourcePage).ToArray());
    }

    [TestMethod]
    public void RejectsZeroOutOfRangeAndOverlappingRanges()
    {
        var pages = new PdfPagePlan(5).Pages;
        foreach (var text in new[] { "0", "6", "4-2", "1,1", "2-4,3", "hello", "1-" })
            Assert.ThrowsExactly<FormatException>(() => PdfPageRange.Parse(text, pages), text);
    }

    [TestMethod]
    public void IgnoresWhitespaceButRetainsExplicitPageOrder()
    {
        var pages = new PdfPagePlan(8).Pages;
        var selected = PdfPageRange.Parse(" 8 , 1 - 3 , 6 ", pages);
        CollectionAssert.AreEqual(new[] { 7, 0, 1, 2, 5 },
            selected.Select(p => p.SourcePage).ToArray());
    }

    [TestMethod]
    public void EmptySelectionAndHugeInputAreRejected()
    {
        var pages = new PdfPagePlan(3).Pages;
        Assert.ThrowsExactly<FormatException>(() => PdfPageRange.Parse(" ", pages));
        Assert.ThrowsExactly<FormatException>(() => PdfPageRange.Parse(new string('1', 4097), pages));
    }
}
