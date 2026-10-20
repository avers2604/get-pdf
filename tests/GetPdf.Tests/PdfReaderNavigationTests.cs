using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfReaderNavigationTests
{
    [DataTestMethod]
    [DataRow("1", 12, 0)]
    [DataRow("12", 12, 11)]
    [DataRow(" 03 ", 12, 2)]
    public void ConvertsVisiblePageNumbersToZeroBasedIndices(
        string input, int count, int expectedIndex)
    {
        Assert.IsTrue(PdfReaderNavigation.TryParsePage(input, count, out int index));
        Assert.AreEqual(expectedIndex, index);
    }

    [DataTestMethod]
    [DataRow("0")]
    [DataRow("13")]
    [DataRow("-1")]
    [DataRow("3-5")]
    [DataRow("три")]
    [DataRow("")]
    public void RejectsOutOfRangeAndNonNumericPages(string input)
    {
        Assert.IsFalse(PdfReaderNavigation.TryParsePage(input, 12, out _));
    }

    [TestMethod]
    public void SearchPreviousWrapsAndRefusesEmptyResults()
    {
        Assert.AreEqual(3, PdfReaderNavigation.PreviousSearchHit(0, 4));
        Assert.AreEqual(1, PdfReaderNavigation.PreviousSearchHit(2, 4));
        Assert.AreEqual(-1, PdfReaderNavigation.PreviousSearchHit(0, 0));
    }
}
