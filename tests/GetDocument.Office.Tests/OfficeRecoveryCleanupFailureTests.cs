using System.Security;
using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27ax deterministic fault injection for cleanup errors. No real
/// user or recovery files are touched by the injected delegates.
/// </summary>
[TestClass]
public sealed class OfficeRecoveryCleanupFailureTests
{
    [TestMethod]
    public void IoCleanupDenialDoesNotReplacePrimaryExportFailure()
    {
        var attempted = 0;
        OfficeRecoveryStore.TryDelete("synthetic.part.docx", _ =>
        {
            attempted++;
            throw new IOException("simulated sharing violation");
        });
        Assert.AreEqual(1, attempted);
    }

    [TestMethod]
    public void AccessDeniedCleanupDoesNotReplacePrimaryExportFailure()
    {
        OfficeRecoveryStore.TryDelete("synthetic.json.tmp",
            _ => throw new UnauthorizedAccessException("synthetic ACL denial"));
    }

    [TestMethod]
    public void SecurityPolicyCleanupDenialIsBestEffort()
    {
        OfficeRecoveryStore.TryDelete("synthetic.part.xlsx",
            _ => throw new SecurityException("synthetic security-policy denial"));
    }

    [TestMethod]
    public void UnexpectedProgrammingErrorsAreNotSilentlyIgnored()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            OfficeRecoveryStore.TryDelete("synthetic.part.pptx",
                _ => throw new InvalidOperationException("synthetic programming defect")));
    }
}
