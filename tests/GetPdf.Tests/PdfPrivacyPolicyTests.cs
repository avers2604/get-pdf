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
    public void HistoryAndFavoritesAreOnByDefaultButTabRestorationIsNot()
    {
        // Since 0.1.2 the user-local history and favourites are on unless IT disables them.
        var policy = WorkstationPolicy.Parse("{\"Version\":1,\"AllowPrinting\":false}");
        Assert.IsTrue(policy.RememberRecentDocuments);
        Assert.IsTrue(policy.AllowFavoriteDocuments);
        Assert.IsFalse(policy.RestoreTabs);
        Assert.IsFalse(policy.AllowPrinting);
        Assert.IsTrue(new WorkstationPolicy().RememberRecentDocuments);
        Assert.IsFalse(new WorkstationPolicy().RestoreTabs);
    }

    [TestMethod]
    public void AdminCanDisableFavoritesSeparately()
    {
        var policy = WorkstationPolicy.Parse("{\"Version\":1,\"AllowFavoriteDocuments\":false}");
        Assert.IsFalse(policy.AllowFavoriteDocuments);
        Assert.IsTrue(policy.RememberRecentDocuments);
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
