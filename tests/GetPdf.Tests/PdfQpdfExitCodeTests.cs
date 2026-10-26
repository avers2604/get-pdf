using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfQpdfExitCodeTests
{
    [TestMethod]
    public void WarningsAreASuccessfulRunButErrorsAreNot()
    {
        // qpdf: 0 = ok, 2 = errors, 3 = ok with warnings (common for scanner/office PDFs).
        Assert.IsTrue(PdfQpdfProcessRunner.Succeeded(0));
        Assert.IsTrue(PdfQpdfProcessRunner.Succeeded(3));
        Assert.IsFalse(PdfQpdfProcessRunner.Succeeded(2));
        Assert.IsFalse(PdfQpdfProcessRunner.Succeeded(1));
        Assert.IsFalse(PdfQpdfProcessRunner.Succeeded(-1));
    }

    [TestMethod]
    public void StructureCheckReportsWarningsSeparatelyAndRejectsErrors()
    {
        Assert.AreEqual(PdfQpdfCheck.Clean, PdfQpdfValidator.Interpret(0));
        Assert.AreEqual(PdfQpdfCheck.Warnings, PdfQpdfValidator.Interpret(3));
        Assert.ThrowsExactly<InvalidDataException>(() => PdfQpdfValidator.Interpret(2));
    }

    [TestMethod]
    public void CompressionResultIsDescribedInKilobytesAndPercent()
    {
        Assert.AreEqual("Сжатая копия «a.pdf»: 163 КБ → 70 КБ (−57 %). Исходный файл не изменён.",
            PdfViewerWindow.DescribeCompression(167_269, 71_179, "a.pdf"));
        StringAssert.Contains(PdfViewerWindow.DescribeCompression(88_913_320, 44_000_000, "scan.pdf"), "84,8 МБ");
    }
}
