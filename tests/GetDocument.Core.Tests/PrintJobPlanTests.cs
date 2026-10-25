using GetDocument.Core.Printing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class PrintJobPlanTests
{
    [TestMethod]
    public void AllPagesProducesStableCupsRange()
    {
        var plan = PrintJobPlan.Create("Office_A4", 8, "", 1, PrintDuplex.OneSided, PrintPaper.A4);
        CollectionAssert.AreEqual(Enumerable.Range(1, 8).ToArray(), plan.Pages.ToArray());
        Assert.AreEqual("1-8", plan.CupsPageRange);
        Assert.AreEqual(8, plan.PageCountToPrint);
    }

    [TestMethod]
    public void StrictPageRangesAreSortedAndDeduplicated()
    {
        var plan = PrintJobPlan.Create("Office_A4", 9, "7,3-5,3,1,4", 2,
            PrintDuplex.LongEdge, PrintPaper.A3);
        CollectionAssert.AreEqual(new[] { 1, 3, 4, 5, 7 }, plan.Pages.ToArray());
        Assert.AreEqual("1,3-5,7", plan.CupsPageRange);
        Assert.AreEqual(10, plan.PageCountToPrint);
        Assert.AreEqual(PrintDuplex.LongEdge, plan.Duplex);
        Assert.AreEqual(PrintPaper.A3, plan.Paper);
    }

    [DataTestMethod]
    [DataRow("0", 10)]
    [DataRow("11", 10)]
    [DataRow("2-", 10)]
    [DataRow("-1", 10)]
    [DataRow("3-2", 10)]
    [DataRow("1,,2", 10)]
    [DataRow("a", 10)]
    [DataRow("1;2", 10)]
    [DataRow("  ", 0)]
    [DataRow("1", 1001)]
    public void InvalidRangeNeverReachesPrinter(string range, int pageCount)
    {
        Assert.ThrowsExactly<ArgumentException>(() => PrintJobPlan.Create(
            "Office_A4", pageCount, range, 1, PrintDuplex.OneSided, PrintPaper.A4));
    }

    [TestMethod]
    public void CopiesAreBoundedAndPrinterNameIsValidated()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            PrintJobPlan.Create("", 3, "", 1, PrintDuplex.OneSided, PrintPaper.A4));
        Assert.ThrowsExactly<ArgumentException>(() =>
            PrintJobPlan.Create("-unauthorized", 3, "", 1, PrintDuplex.OneSided, PrintPaper.A4));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PrintJobPlan.Create("Office", 3, "", 0, PrintDuplex.OneSided, PrintPaper.A4));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PrintJobPlan.Create("Office", 3, "", 100, PrintDuplex.OneSided, PrintPaper.A4));
    }

    [TestMethod]
    public void MaliciousOrHugePageExpressionsAreRejected()
    {
        foreach (string pages in new[] { "--help", "1-999999999999", "2 3",
                     new string('1', 300), "1/2", "1-2-3" })
            Assert.ThrowsExactly<ArgumentException>(() =>
                PrintJobPlan.Create("Printer", 9, pages, 1, PrintDuplex.OneSided, PrintPaper.A4));
    }

    [TestMethod]
    public void SinglePageIsPrintedOnceAndCupsRangeIsLiteral()
    {
        var plan = PrintJobPlan.Create("Printer", 10, "4-4", 1, PrintDuplex.ShortEdge, PrintPaper.A4);
        Assert.AreEqual("4", plan.CupsPageRange);
        Assert.AreEqual(4, plan.Pages.Single());
        Assert.AreEqual(1, plan.PageCountToPrint);
    }
}
