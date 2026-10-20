using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfLosslessOptimizationTests
{
    [TestMethod]
    public void UsesApprovedNoLossCompressionWithoutImageReencoding()
    {
        string folder = Path.GetTempPath();
        var args = PdfLosslessOptimizer.Arguments(
            Path.Combine(folder, "old.pdf"), Path.Combine(folder, "new.pdf"));
        CollectionAssert.AreEqual(new[]
        {
            "--stream-data=compress", "--object-streams=generate",
            Path.Combine(folder, "old.pdf"), Path.Combine(folder, "new.pdf")
        }, args.ToArray());
        Assert.IsFalse(args.Contains("--optimize-images"));
        Assert.IsFalse(args.Contains("--jpeg-quality"));
    }
}
