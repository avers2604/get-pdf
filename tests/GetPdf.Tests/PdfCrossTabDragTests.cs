using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfCrossTabDragTests
{
    [TestMethod]
    public void DragKeepsPageIdentityWhenVisiblePagesAreReordered()
    {
        var plan = new PdfPagePlan(3);
        Guid id = plan.Pages[1].Id;
        plan.Move(1, 2);
        int selected = PdfCrossTabPageTransfer.FindPageById(plan.Pages, id);
        Assert.AreEqual(2, selected);
        Assert.AreEqual(1, plan.Pages[selected].SourcePage);
    }

    [TestMethod]
    public void DragFromRemovedPageIsRejectedWithoutGuessingAnotherPage()
    {
        var plan = new PdfPagePlan(2);
        Guid id = plan.Pages[1].Id;
        plan.Delete(1);
        Assert.AreEqual(-1, PdfCrossTabPageTransfer.FindPageById(plan.Pages, id));
    }
}
