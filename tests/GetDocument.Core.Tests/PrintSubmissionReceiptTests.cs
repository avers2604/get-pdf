using GetDocument.Core.Printing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class PrintSubmissionReceiptTests
{
    [TestMethod]
    public void CupsReceiptIsBoundToExpectedQueueAndNumericJobId()
    {
        string reply = "request id is GET_SIMULATOR-42 (1 file(s))\n";
        Assert.AreEqual("GET_SIMULATOR-42",
            PrintSubmissionReceipt.CupsJobId(reply, "GET_SIMULATOR"));
    }

    [TestMethod]
    public void SupportsHyphenatedPrinterNameWithoutConfusingJobId()
    {
        Assert.AreEqual("GET-TRAM-202",
            PrintSubmissionReceipt.CupsJobId(
                "request id is GET-TRAM-202 (1 file(s))\n", "GET-TRAM"));
    }

    [TestMethod]
    public void DoesNotTrustUnrelatedOrMalformedCupsOutput()
    {
        foreach (string reply in new[] {
            "", "queued", "printer-ready", "request id is OTHER-42 (1 file(s))",
            "request id is GET_SIMULATOR-abc (1 file(s))",
            "request id is GET_SIMULATOR-42\nINJECTED",
            "request id is GET_SIMULATOR-999999999999999999999"
        })
            Assert.IsNull(PrintSubmissionReceipt.CupsJobId(reply, "GET_SIMULATOR"));
    }

    [TestMethod]
    public void WindowsWorkerAcknowledgesOnlyExactLocalMarker()
    {
        Assert.IsTrue(PrintSubmissionReceipt.WindowsAccepted("submitted"));
        Assert.IsTrue(PrintSubmissionReceipt.WindowsAccepted("submitted\n"));
        Assert.IsFalse(PrintSubmissionReceipt.WindowsAccepted("submitted\nqueued elsewhere"));
        Assert.IsFalse(PrintSubmissionReceipt.WindowsAccepted(""));
    }
}
