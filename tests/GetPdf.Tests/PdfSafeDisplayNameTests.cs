using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfSafeDisplayNameTests
{
    [TestMethod]
    public void HiddenBidiAndZeroWidthFormattingCannotSpoofPdfTitle()
    {
        var unsafeName = "Отчёт" + '\u202e' + "fdp.exe" + '\u200b' + ".pdf";
        Assert.AreEqual("Отчётfdp.exe.pdf", PdfSafeDisplayName.Normalize(unsafeName));
        Assert.IsFalse(PdfSafeDisplayName.Normalize(unsafeName).Contains('\u202e'));
    }

    [TestMethod]
    public void RussianNameIsPreservedAndMissingLabelsHaveGenericFallback()
    {
        Assert.AreEqual("Приказ № 15.pdf", PdfSafeDisplayName.Normalize(" Приказ № 15.pdf "));
        Assert.AreEqual("Документ PDF", PdfSafeDisplayName.Normalize(null));
        Assert.AreEqual("Документ PDF", PdfSafeDisplayName.Normalize(
            "\u200b\u202e\u200f"));
        Assert.AreEqual("PDF", PdfSafeDisplayName.Normalize("P\nD\rF"));
    }

    [TestMethod]
    public void ExtremelyLongDisplayNameIsBoundedWithoutRenamingFile()
    {
        string original = new string('Ж', 200) + ".pdf";
        string displayed = PdfSafeDisplayName.Normalize(original);
        Assert.AreEqual(160, displayed.Length);
        Assert.AreEqual(204, original.Length);
    }
}
