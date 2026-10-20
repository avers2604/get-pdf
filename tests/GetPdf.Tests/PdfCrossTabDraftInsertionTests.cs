using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfCrossTabDraftInsertionTests
{
    [TestMethod]
    public void InsertedExternalSourcePageCanBeUndoneAndRedoneWithoutLosingSource()
    {
        var plan = new PdfPagePlan(3);
        var history = new PdfEditHistory(plan);
        Guid before = plan.Pages[1].Id;
        int appendedSourceIndex = plan.RegisterSourcePage();

        history.Execute(p => p.InsertSourcePageAfter(1, appendedSourceIndex));
        Assert.HasCount(4, plan.Pages);
        Assert.AreEqual(appendedSourceIndex, plan.Pages[2].SourcePage);
        Assert.AreNotEqual(before, plan.Pages[2].Id);
        Assert.IsTrue(plan.HasChanges);

        Assert.IsTrue(history.Undo());
        Assert.HasCount(3, plan.Pages);
        Assert.AreEqual(before, plan.Pages[1].Id);
        Assert.IsTrue(history.Redo());
        Assert.HasCount(4, plan.Pages);
        Assert.AreEqual(appendedSourceIndex, plan.Pages[2].SourcePage);
    }

    [TestMethod]
    public void InsertRejectsMissingSourcePageWithoutCorruptingUndoHistory()
    {
        var plan = new PdfPagePlan(2);
        var history = new PdfEditHistory(plan);
        var before = plan.Snapshot();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            history.Execute(p => p.InsertSourcePageAfter(0, 2)));
        CollectionAssert.AreEqual(before, plan.Snapshot());
        Assert.IsFalse(history.CanUndo);
    }

    [TestMethod]
    public void InsertPreservesExistingVisibleMarkupAndEditedOrdering()
    {
        var plan = new PdfPagePlan(3);
        plan.SetStamp(2, "Проверено");
        plan.Move(2, 0);
        int newSource = plan.RegisterSourcePage();
        var history = new PdfEditHistory(plan);

        history.Execute(p => p.InsertSourcePageAfter(0, newSource));
        Assert.AreEqual("Проверено", plan.Pages[0].Stamp);
        Assert.AreEqual(newSource, plan.Pages[1].SourcePage);
        Assert.AreEqual(string.Empty, plan.Pages[1].Stamp);
        Assert.IsTrue(history.Undo());
        Assert.AreEqual("Проверено", plan.Pages[0].Stamp);
        Assert.HasCount(3, plan.Pages);
    }

    [TestMethod]
    public void RejectsOverflowBeforeAllocatingAnyNewPageReference()
    {
        var plan = new PdfPagePlan(1);
        var count = plan.SourcePageCount;
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            plan.RegisterSourcePage(1));
        Assert.AreEqual(count, plan.SourcePageCount);
    }
}
