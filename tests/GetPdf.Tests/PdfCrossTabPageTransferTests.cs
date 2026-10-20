using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfCrossTabPageTransferTests
{
    [TestMethod]
    public void CopiesSourcePageImmediatelyAfterSelectedDestinationPage()
    {
        string folder = Path.GetTempPath();
        string first = Path.Combine(folder, "one.pdf");
        string other = Path.Combine(folder, "two.pdf");
        PdfPageReference[] target =
            [new(0, 0), new(1, 0), new(2, 0)];
        var result = PdfCrossTabPageTransfer.Build(
            first, target, 1, 3, other, new PdfPageReference(4, 0), 5, maxPages: 10);
        Assert.HasCount(4, result);
        CollectionAssert.AreEqual(new[] { 0, 1, 4, 2 },
            result.Select(x => x.SourcePage).ToArray());
        CollectionAssert.AreEqual(new[] { first, first, other, first },
            result.Select(x => x.SourcePath).ToArray());
    }

    [TestMethod]
    public void RejectsSameSourceAndUnsafeMarkupsInsteadOfDroppingEdits()
    {
        string folder = Path.GetTempPath();
        string first = Path.Combine(folder, "one.pdf");
        string other = Path.Combine(folder, "two.pdf");
        var marked = new PdfPageReference(0, 0, Stamp: "КОПИЯ");
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            PdfCrossTabPageTransfer.Build(first, [marked], 0, 1,
                other, new PdfPageReference(0, 0), 1, 10));
        Assert.ThrowsExactly<ArgumentException>(() =>
            PdfCrossTabPageTransfer.Build(first, [new(0, 0)], 0, 1,
                first, new PdfPageReference(0, 0), 1, 10));
    }

    [TestMethod]
    public void RejectsOutOfBoundsAndMaximumPagesBeforeAnyFileIsOpened()
    {
        string folder = Path.GetTempPath();
        string first = Path.Combine(folder, "one.pdf");
        string other = Path.Combine(folder, "two.pdf");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PdfCrossTabPageTransfer.Build(first, [new(0, 0)], 1, 1,
                other, new PdfPageReference(0, 0), 1, 10));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            PdfCrossTabPageTransfer.Build(first, [new(0, 0)], 0, 1,
                other, new PdfPageReference(0, 0), 1, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PdfCrossTabPageTransfer.Build(first, [new(0, 0)], 0, 1,
                other, new PdfPageReference(50, 0), 1, 10));
    }

    [TestMethod]
    public void NeverExportsStampsOrRotationsWithoutExplicitSupport()
    {
        string first = Path.Combine(Path.GetTempPath(), "first.pdf");
        string second = Path.Combine(Path.GetTempPath(), "second.pdf");
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            PdfCrossTabPageTransfer.Build(first, [new(0, 0)], 0, 1,
                second, new PdfPageReference(0, 90), 1, 10));
    }
}
