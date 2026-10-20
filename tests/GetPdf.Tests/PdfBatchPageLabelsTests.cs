using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfBatchPageLabelsTests
{
    [TestMethod]
    public void PageNumbersAreBoundedAndOrdered()
    {
        Assert.AreEqual("Стр. 1 / 20", PdfBatchPageLabels.Number(1, 20));
        Assert.AreEqual("Стр. 20 / 20", PdfBatchPageLabels.Number(20, 20));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfBatchPageLabels.Number(0, 20));
        Assert.ThrowsExactly<ArgumentException>(() => PdfBatchPageLabels.Number(1, 10, "A\n"));
    }

    [TestMethod]
    public void SplitPlanNeverRepeatsAndHasDefinedLimit()
    {
        var sample = Path.Combine(Path.GetTempPath(), "part.pdf");
        var names = PdfSplitPageNaming.Plan(sample, 3);
        CollectionAssert.AreEqual(new[]
        {
            Path.Combine(Path.GetTempPath(), "part_001.pdf"),
            Path.Combine(Path.GetTempPath(), "part_002.pdf"),
            Path.Combine(Path.GetTempPath(), "part_003.pdf")
        }, names);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PdfSplitPageNaming.Plan(sample, PdfSplitPageNaming.MaxPerOperation + 1));
    }
}
