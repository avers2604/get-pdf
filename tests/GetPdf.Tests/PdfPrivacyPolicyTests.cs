using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfPrivacyPolicyTests
{
    [TestMethod]
    public void AdminCanDisablePathHistoryAndTabRestorationIndependently()
    {
        var policy = WorkstationPolicy.Parse(
            "{\"Version\":1,\"RememberRecentDocuments\":false,\"RestoreTabs\":false}");
        Assert.IsFalse(policy.RememberRecentDocuments);
        Assert.IsFalse(policy.RestoreTabs);
        Assert.IsTrue(policy.AllowPdfViewer);
        Assert.IsTrue(policy.AllowPrinting);
    }

    [TestMethod]
    public void MissingNewPrivacyFlagsKeepCompatibleDefaults()
    {
        var policy = WorkstationPolicy.Parse("{\"Version\":1,\"AllowPrinting\":false}");
        Assert.IsTrue(policy.RememberRecentDocuments);
        Assert.IsTrue(policy.RestoreTabs);
        Assert.IsFalse(policy.AllowPrinting);
    }
}
