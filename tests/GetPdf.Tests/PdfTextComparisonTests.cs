using Microsoft.VisualStudio.TestTools.UnitTesting;
using GetPdf;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfTextComparisonTests
{
    [TestMethod]
    public void AlignsInsertionAndDeletionWithoutMislabelingUnaffectedPages()
    {
        var diff = PdfTextComparisonService.Compare(["А", "Б", "В"], ["А", "Дополнение", "Б", "В"]);
        Assert.AreEqual(3, diff.Changes.Count(c => c.Type == PdfTextComparisonService.Kind.EqualText));
        Assert.AreEqual(1, diff.Changes.Count(c => c.Type == PdfTextComparisonService.Kind.Added));
        Assert.AreEqual(0, diff.Changes.Count(c => c.Type == PdfTextComparisonService.Kind.ChangedText));
    }

    [TestMethod]
    public void ReplacedTextIsChangedAndEmptyLayersAreNotDeclaredEqual()
    {
        var diff = PdfTextComparisonService.Compare(["Страница", ""], ["Изменённая", ""]);
        Assert.AreEqual(1, diff.Changes.Count(c => c.Type == PdfTextComparisonService.Kind.ChangedText));
        Assert.AreEqual(1, diff.Changes.Count(c => c.Type == PdfTextComparisonService.Kind.UnverifiedImage));
    }

    [TestMethod]
    public void NormalizesWhitespaceAndLimitsPages()
    {
        var diff = PdfTextComparisonService.Compare(["A  B\r\nC"], ["A B C"]);
        Assert.AreEqual(PdfTextComparisonService.Kind.EqualText, diff.Changes[0].Type);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            PdfTextComparisonService.Compare(Enumerable.Repeat("x", 1001).ToArray(), ["x"]));
    }
}