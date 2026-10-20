using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf;

[TestClass]
public sealed class PdfWorkspaceLayoutTests
{
    [TestMethod]
    public void NarrowWorkspaceInitiallyHidesPagesToKeepPreviewAvailable()
    {
        Assert.IsFalse(PdfWorkspaceLayout.ShowPages(680));
        Assert.IsFalse(PdfWorkspaceLayout.ShowPages(959));
        Assert.IsTrue(PdfWorkspaceLayout.ShowPages(960));
        Assert.IsTrue(PdfWorkspaceLayout.ShowPages(1280));
    }

    [TestMethod]
    public void OperatorOverrideWinsOverAutomaticBreakpoint()
    {
        Assert.IsTrue(PdfWorkspaceLayout.ShowPages(680, true));
        Assert.IsFalse(PdfWorkspaceLayout.ShowPages(1280, false));
        Assert.IsTrue(PdfWorkspaceLayout.ShowPages(1280, null));
    }

    [TestMethod]
    public void InvalidViewportDoesNotExposeUnexpectedWideLayout()
    {
        Assert.IsFalse(PdfWorkspaceLayout.ShowPages(double.NaN));
        Assert.IsFalse(PdfWorkspaceLayout.ShowPages(double.PositiveInfinity));
        Assert.IsFalse(PdfWorkspaceLayout.ShowPages(-1));
        Assert.IsTrue(PdfWorkspaceLayout.ShowPages(double.NaN, true));
    }

    [TestMethod]
    public void SearchResultsHeightUsesBoundedAreaOnSmallAndLargeWindows()
    {
        Assert.AreEqual(72.0, PdfWorkspaceLayout.ResultsHeight(220));
        Assert.AreEqual(118.8, PdfWorkspaceLayout.ResultsHeight(540), 0.001);
        Assert.AreEqual(185.0, PdfWorkspaceLayout.ResultsHeight(1100));
        Assert.AreEqual(72.0, PdfWorkspaceLayout.ResultsHeight(double.NaN));
    }
}
