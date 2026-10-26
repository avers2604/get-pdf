using GetDocument.Core.Printing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class PrintSpoolFailurePolicyTests
{
    [DataTestMethod]
    [DataRow(2, PrintFailureKind.InvalidRequest, false)]
    [DataRow(3, PrintFailureKind.SourceOrPrinterChanged, false)]
    [DataRow(4, PrintFailureKind.UnsupportedSettings, false)]
    [DataRow(5, PrintFailureKind.Indeterminate, true)]
    [DataRow(81, PrintFailureKind.Indeterminate, true)]
    public void WindowsWorkerExitCodesAreCategorizedConservatively(
        int exit, PrintFailureKind expected, bool mayBeQueued)
    {
        PrintFailure result = PrintSpoolFailurePolicy.FromWindowsWorker(exit);
        Assert.AreEqual(expected, result.Kind);
        Assert.AreEqual(mayBeQueued, result.MightBeQueued);
        Assert.IsFalse(result.UserMessage.Contains("C:\\", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LinuxLpFailureNeverClaimsPhysicalPrintOrSafeRetry()
    {
        var result = PrintSpoolFailurePolicy.FromCupsFailure();
        Assert.AreEqual(PrintFailureKind.Indeterminate, result.Kind);
        Assert.IsTrue(result.MightBeQueued);
        StringAssert.Contains(result.UserMessage, "очеред");
    }

    [TestMethod]
    public void TimeoutAndMissingSpoolerAreDifferent()
    {
        var timeout = PrintSpoolFailurePolicy.TimedOut();
        var missing = PrintSpoolFailurePolicy.UnavailableBeforeSubmit();
        Assert.IsTrue(timeout.MightBeQueued);
        Assert.IsFalse(missing.MightBeQueued);
        Assert.AreEqual(PrintFailureKind.TransportUnavailable, missing.Kind);
    }

    [TestMethod]
    public void KnownRejectionDoesNotPretendDocumentWasPrinted()
    {
        var result = PrintSpoolFailurePolicy.FromWindowsWorker(4);
        StringAssert.Contains(result.UserMessage, "не поддерж");
        Assert.IsFalse(result.UserMessage.Contains("распечатан", StringComparison.OrdinalIgnoreCase));
    }
}
