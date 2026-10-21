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
    public void MissingPrivacyFlagsDoNotPersistSensitivePdfPaths()
    {
        var policy = WorkstationPolicy.Parse("{\"Version\":1,\"AllowPrinting\":false}");
        Assert.IsFalse(policy.RememberRecentDocuments);
        Assert.IsFalse(policy.RestoreTabs);
        Assert.IsFalse(policy.AllowPrinting);
        Assert.IsFalse(new WorkstationPolicy().RememberRecentDocuments);
        Assert.IsFalse(new WorkstationPolicy().RestoreTabs);
    }

    [TestMethod]
    public void ManagedItPolicyCanExplicitlyEnablePathHistoryAndSessionRestoration()
    {
        var policy = WorkstationPolicy.Parse(
            "{\"Version\":1,\"RememberRecentDocuments\":true,\"RestoreTabs\":true}");
        Assert.IsTrue(policy.RememberRecentDocuments);
        Assert.IsTrue(policy.RestoreTabs);
        Assert.IsTrue(policy.AllowPdfViewer);
        Assert.IsTrue(policy.AllowPrinting);
    }
}
