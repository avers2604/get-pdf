using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeTabRegistryTests
{
    private const string SessionA = "e38a9a2f-9b21-4dad-8f20-28ad39d0b31f";
    private const string SessionB = "823b6c03-2faf-4c45-95c8-b4dc8c069423";

    [TestMethod]
    public void TwoTabsAreIsolatedAndNeverGainSaveOrPrintPermissions()
    {
        var tabs = new EuroOfficeNativeTabRegistry();
        tabs.Register("tab-a", "doc-a", SessionA);
        tabs.Register("tab-b", "doc-b", SessionB);

        tabs.Apply("tab-a", new EuroOfficeNativeIpcFrame(
            "event", SessionA, 1, "view-loaded", 10, null));
        tabs.Apply("tab-a", new EuroOfficeNativeIpcFrame(
            "event", SessionA, 2, "on-save", 3002, null));

        var first = tabs.Snapshot("tab-a");
        var second = tabs.Snapshot("tab-b");
        Assert.AreEqual(EuroOfficeNativeDocumentStatus.ViewLoadedUnverified, first.Status);
        Assert.AreEqual(EuroOfficeNativeDocumentStatus.Requested, second.Status);
        Assert.AreEqual(2L, first.LastSequence);
        Assert.AreEqual(0L, second.LastSequence);
        Assert.IsFalse(first.DocumentVerified);
        Assert.IsFalse(first.SaveCompleted);
        Assert.IsFalse(first.PdfReady);
        Assert.IsFalse(first.PrintApproved);
        Assert.IsTrue(first.SaveNotificationObserved);
        Assert.IsFalse(second.SaveNotificationObserved);
    }

    [TestMethod]
    public void ReplayGapForeignSessionAndUnsupportedTabAreRejected()
    {
        var tabs = new EuroOfficeNativeTabRegistry();
        tabs.Register("tab-a", "doc-a", SessionA);
        var opened = new EuroOfficeNativeIpcFrame(
            "event", SessionA, 1, "view-loaded", 10, null);
        tabs.Apply("tab-a", opened);
        Assert.ThrowsExactly<InvalidDataException>(() => tabs.Apply("tab-a", opened));
        Assert.ThrowsExactly<InvalidDataException>(() => tabs.Apply("tab-a",
            opened with { Sequence = 3 }));
        Assert.ThrowsExactly<InvalidDataException>(() => tabs.Apply("tab-a",
            opened with { SessionId = SessionB, Sequence = 2 }));
        Assert.ThrowsExactly<KeyNotFoundException>(() => tabs.Apply("tab-missing",
            opened with { Sequence = 2 }));
        Assert.AreEqual(1L, tabs.Snapshot("tab-a").LastSequence);
    }

    [TestMethod]
    public void ClosureIsTerminalAndRequiresExplicitRemoval()
    {
        var tabs = new EuroOfficeNativeTabRegistry();
        tabs.Register("tab-a", "doc-a", SessionA);
        tabs.Apply("tab-a", new("event", SessionA, 1, "closed", 2001, null));
        Assert.AreEqual(EuroOfficeNativeDocumentStatus.Closed, tabs.Snapshot("tab-a").Status);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            tabs.Apply("tab-a", new("event", SessionA, 2, "view-loaded", 10, null)));
        Assert.IsTrue(tabs.RemoveTerminal("tab-a"));
        Assert.AreEqual(0, tabs.Count);
    }

    [TestMethod]
    public void AbortedHostMarksTabFailedAndAllowsCleanup()
    {
        var tabs = new EuroOfficeNativeTabRegistry();
        tabs.Register("tab-a", "doc-a", SessionA);
        tabs.Abort("tab-a", "native-process-crash");
        Assert.AreEqual(EuroOfficeNativeDocumentStatus.Failed, tabs.Snapshot("tab-a").Status);
        Assert.IsTrue(tabs.RemoveTerminal("tab-a"));
    }

    [TestMethod]
    public void DuplicateTabsAndOneSessionBoundToTwoTabsAreForbidden()
    {
        var tabs = new EuroOfficeNativeTabRegistry();
        tabs.Register("tab-a", "doc-a", SessionA);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            tabs.Register("tab-a", "doc-other", SessionB));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            tabs.Register("tab-b", "doc-b", SessionA));
    }

    [TestMethod]
    public void RegistrationLimitAvoidsUnboundedNativeProcesses()
    {
        var tabs = new EuroOfficeNativeTabRegistry(maxTabs: 2);
        tabs.Register("tab-a", "doc-a", SessionA);
        tabs.Register("tab-b", "doc-b", SessionB);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            tabs.Register("tab-c", "doc-c", Guid.NewGuid().ToString("D")));
    }

    [TestMethod]
    public void CannotRemoveActiveSessionWithoutRecordingTermination()
    {
        var tabs = new EuroOfficeNativeTabRegistry();
        tabs.Register("tab-a", "doc-a", SessionA);
        Assert.ThrowsExactly<InvalidOperationException>(() => tabs.RemoveTerminal("tab-a"));
        tabs.Abort("tab-a", "operator-requested-termination");
        Assert.IsTrue(tabs.RemoveTerminal("tab-a"));
    }
}
