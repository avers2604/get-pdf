using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeWorkspaceTabsTests
{
    [TestMethod]
    public void NativeTabsShareOneLogicalStripButNotLibreOfficeWorkspaceIds()
    {
        var tabs = new EuroOfficeNativeWorkspaceTabs();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        Guid sessionA = Guid.NewGuid(), sessionB = Guid.NewGuid();
        tabs.Register(a, sessionA, "synthetic-A", "Тестовый документ");
        tabs.Register(b, sessionB, "synthetic-B", "Тестовая таблица");

        Assert.AreEqual(2, tabs.TabCount);
        Assert.IsNull(tabs.ActiveTabId);
        tabs.Select(a);
        Assert.AreEqual(a, tabs.ActiveTabId);
        Assert.IsTrue(tabs.Get(a).Selected);
        Assert.IsFalse(tabs.Get(b).Selected);
        tabs.Select(b);
        Assert.AreEqual(b, tabs.ActiveTabId);
        Assert.IsFalse(tabs.Get(a).Selected);
        Assert.IsTrue(tabs.Get(b).Selected);
        Assert.IsFalse(tabs.Get(b).DocumentVerified);
        Assert.IsFalse(tabs.Get(b).SaveCompleted);
        Assert.IsFalse(tabs.Get(b).PdfReady);
    }

    [TestMethod]
    public void NativeReceiptDoesNotGrantSaveOrPrintAndIsConfinedToSession()
    {
        var tabs = new EuroOfficeNativeWorkspaceTabs();
        Guid id = Guid.NewGuid(), session = Guid.NewGuid();
        tabs.Register(id, session, "fixture", "Лаборатория");
        var receipt = new EuroOfficeNativeReceipt(
            ViewLoaded: true, ModifiedNotificationObserved: true,
            SaveNotificationObserved: true, SaveCancelled: false,
            PrintNotificationObserved: true);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            tabs.ApplyReceipt(id, Guid.NewGuid(), receipt));
        tabs.ApplyReceipt(id, session, receipt);
        var snapshot = tabs.Get(id);
        Assert.AreEqual(EuroOfficeNativeDocumentStatus.ViewLoadedUnverified, snapshot.Status);
        Assert.IsTrue(snapshot.ModifiedObserved);
        Assert.IsFalse(snapshot.DocumentVerified);
        Assert.IsFalse(snapshot.SaveCompleted);
        Assert.IsFalse(snapshot.PdfReady);
        Assert.IsFalse(snapshot.PrintApproved);
    }

    [TestMethod]
    public void CrashVisibleWithNoImplicitRestartOrLossOfOtherTab()
    {
        var tabs = new EuroOfficeNativeWorkspaceTabs();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        Guid oldHost = Guid.NewGuid(), otherHost = Guid.NewGuid();
        tabs.Register(a, oldHost, "fixture-a", "Первый");
        tabs.Register(b, otherHost, "fixture-b", "Второй");
        tabs.Select(a);
        tabs.HostCrashed(a, oldHost);

        Assert.AreEqual(EuroOfficeNativeDocumentStatus.Failed, tabs.Get(a).Status);
        Assert.AreEqual(a, tabs.ActiveTabId);
        Assert.AreEqual(EuroOfficeNativeDocumentStatus.Requested, tabs.Get(b).Status);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            tabs.ApplyReceipt(a, Guid.NewGuid(),
                new EuroOfficeNativeReceipt(true, false, false, false, false)));

        Guid newHost = Guid.NewGuid();
        tabs.Restart(a, oldHost, newHost);
        Assert.AreEqual(2, tabs.Get(a).Generation);
        Assert.AreEqual(newHost, tabs.Get(a).SessionId);
        Assert.AreEqual(EuroOfficeNativeDocumentStatus.Requested, tabs.Get(a).Status);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            tabs.ApplyReceipt(a, oldHost,
                new EuroOfficeNativeReceipt(true, false, false, false, false)));
    }

    [TestMethod]
    public void ClosingFailedTabIsAllowedButCannotAffectOtherTabs()
    {
        var tabs = new EuroOfficeNativeWorkspaceTabs();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        Guid sa = Guid.NewGuid(), sb = Guid.NewGuid();
        tabs.Register(a, sa, "a", "A");
        tabs.Register(b, sb, "b", "B");
        tabs.Select(a);
        tabs.HostCrashed(a, sa);
        tabs.Close(a, sa);
        Assert.AreEqual(1, tabs.TabCount);
        Assert.IsNull(tabs.ActiveTabId);
        Assert.ThrowsExactly<KeyNotFoundException>(() => tabs.Get(a));
        tabs.Select(b);
        Assert.AreEqual(b, tabs.ActiveTabId);
    }

    [TestMethod]
    public void CloseActiveTabAfterSaveNotificationDoesNotClaimARevision()
    {
        var tabs = new EuroOfficeNativeWorkspaceTabs();
        Guid a = Guid.NewGuid(), session = Guid.NewGuid();
        tabs.Register(a, session, "a", "A");
        tabs.ApplyReceipt(a, session, new EuroOfficeNativeReceipt(
            false, true, true, false, false));
        Assert.IsTrue(tabs.Get(a).ModifiedObserved);
        Assert.IsFalse(tabs.Get(a).SaveCompleted);
        tabs.Close(a, session);
        Assert.AreEqual(0, tabs.TabCount);
        Assert.ThrowsExactly<KeyNotFoundException>(() => tabs.Get(a));
    }

    [TestMethod]
    public void RejectsDuplicateTabBlankTitleAndUnmatchedCloseOrRestart()
    {
        var tabs = new EuroOfficeNativeWorkspaceTabs();
        Guid a = Guid.NewGuid(), sa = Guid.NewGuid();
        Assert.ThrowsExactly<ArgumentException>(() =>
            tabs.Register(a, sa, "doc", ""));
        tabs.Register(a, sa, "doc", "Тест");
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            tabs.Register(a, Guid.NewGuid(), "doc", "Повтор"));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            tabs.Close(a, Guid.NewGuid()));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            tabs.Restart(a, sa, Guid.NewGuid()));
        Assert.ThrowsExactly<KeyNotFoundException>(() =>
            tabs.Select(Guid.NewGuid()));
    }
}
