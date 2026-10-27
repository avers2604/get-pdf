using GetDocument.Core.Platforms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public class PlatformContractTests
{
    [TestMethod]
    public void PrintRequestAcceptsSupportedCopies()
    {
        new DocumentPrintRequest("out.pdf", 1).Validate();
        new DocumentPrintRequest("out.PDF", 99, true).Validate();
    }
    [TestMethod]
    public void PrintRequestRejectsNonPdfAndEmptyInputs()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new DocumentPrintRequest("file.docx").Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new DocumentPrintRequest("").Validate());
    }
    [TestMethod]
    public void PrintRequestRejectsOutOfRangeCopies()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DocumentPrintRequest("file.pdf", 0).Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DocumentPrintRequest("file.pdf", 100).Validate());
    }
}
