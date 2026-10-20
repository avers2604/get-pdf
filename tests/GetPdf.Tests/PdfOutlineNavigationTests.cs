using Microsoft.VisualStudio.TestTools.UnitTesting;
using PdfSharp.Pdf;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfOutlineNavigationTests
{
    [TestMethod]
    public void ReadsRealPdfOutlineWithOriginalSourcePageIndices()
    {
        string folder = Path.Combine(Path.GetTempPath(), "getpdf-outline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "document.pdf");
            using (var pdf = new PdfDocument())
            {
                var first = pdf.AddPage();
                var second = pdf.AddPage();
                pdf.Outlines.Add("Введение", first);
                pdf.Outlines.Add("Приложение", second);
                pdf.Save(path);
            }
            var nodes = PdfOutlineReader.Read(path);
            Assert.HasCount(2, nodes);
            Assert.AreEqual("Введение", nodes[0].Title);
            Assert.AreEqual(0, nodes[0].SourcePageIndex);
            Assert.AreEqual("Приложение", nodes[1].Title);
            Assert.AreEqual(1, nodes[1].SourcePageIndex);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void NavigationMapsOriginalPageToCurrentEditedOrder()
    {
        using var _ = new PdfDocument();
        var plan = new PdfPagePlan(3);
        plan.Move(2, 0);
        Assert.AreEqual(0, PdfOutlineReader.VisibleIndex(plan.Pages, 2));
        Assert.AreEqual(1, PdfOutlineReader.VisibleIndex(plan.Pages, 0));
        plan.Delete(1);
        Assert.AreEqual(-1, PdfOutlineReader.VisibleIndex(plan.Pages, 0));
    }

    [TestMethod]
    public void EmptyPdfWithoutOutlinesIsSafeAndTitlesAreSanitized()
    {
        string folder = Path.Combine(Path.GetTempPath(), "getpdf-no-outline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "plain.pdf");
            using (var pdf = new PdfDocument()) { pdf.AddPage(); pdf.Save(path); }
            Assert.HasCount(0, PdfOutlineReader.Read(path));
            Assert.AreEqual("Глава 1", PdfOutlineReader.SafeTitle("Глава\n1\u0001"));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
