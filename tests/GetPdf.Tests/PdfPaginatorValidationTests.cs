using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Windows;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfPaginatorValidationTests
{
    [TestMethod]
    public void RejectsMissingAndOutOfRangeRasterSourcesBeforeSpooling()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new PdfPagePrintPaginator([], [new PdfPageReference(0, 0)],
                new Size(794, 1123)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new PdfPagePrintPaginator(["page.png"], [new PdfPageReference(2, 0)],
                new Size(794, 1123)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new PdfPagePrintPaginator(["page.png"], [new PdfPageReference(0, 0)],
                new Size(0, 1123)));
    }

    [TestMethod]
    public void CapturesPrintPlanToPreventSubsequentSourceMutation()
    {
        var names = new List<string> { "first.png" };
        var pages = new List<PdfPageReference> { new(0, 0) };
        var paginator = new PdfPagePrintPaginator(names, pages, new Size(794, 1123));
        names.Clear();
        pages.Clear();
        Assert.AreEqual(1, paginator.PageCount);
    }
}
