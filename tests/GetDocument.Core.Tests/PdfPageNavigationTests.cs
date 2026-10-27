using GetDocument.Core.Preview;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class PdfPageNavigationTests
{
    [TestMethod]
    public void FirstAndLastPageAreOneBasedForTheOperator()
    {
        Assert.IsTrue(PdfPageNavigation.TryParsePage("1", 10, out var first));
        Assert.AreEqual(0, first);
        Assert.IsTrue(PdfPageNavigation.TryParsePage("10", 10, out var last));
        Assert.AreEqual(9, last);
    }

    [DataTestMethod]
    [DataRow(null, 10)]
    [DataRow("", 10)]
    [DataRow("0", 10)]
    [DataRow("-1", 10)]
    [DataRow("11", 10)]
    [DataRow("1x", 10)]
    [DataRow("1.5", 10)]
    [DataRow("999999999999999999", 10)]
    [DataRow("1", 0)]
    [DataRow("1", 1001)]
    public void UntrustedOrOutOfRangePageNumbersAreRejected(string? value, int count)
    {
        Assert.IsFalse(PdfPageNavigation.TryParsePage(value, count, out _));
    }

    [TestMethod]
    public void WhitespaceIsAcceptedButExtraCharactersAreNot()
    {
        Assert.IsTrue(PdfPageNavigation.TryParsePage(" 4 ", 7, out int index));
        Assert.AreEqual(3, index);
        Assert.IsFalse(PdfPageNavigation.TryParsePage("4 page", 7, out _));
    }

    [TestMethod]
    public void NavigationIsClampedToDocumentBounds()
    {
        Assert.AreEqual(0, PdfPageNavigation.Move(0, -1, 8));
        Assert.AreEqual(7, PdfPageNavigation.Move(7, 1, 8));
        Assert.AreEqual(7, PdfPageNavigation.Move(3, 10000, 8));
        Assert.AreEqual(0, PdfPageNavigation.Move(3, -10000, 8));
        Assert.AreEqual(4, PdfPageNavigation.Move(3, 1, 8));
    }

    [TestMethod]
    public void InvalidNavigationStateFailsSafely()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => PdfPageNavigation.Move(0, 1, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfPageNavigation.Move(9, 1, 4));
    }
}
