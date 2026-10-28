using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeTabRegistryTests
{
    [TestMethod]
    public void MultipleTabsKeepIndependentSessionsAndNoVerifiedEdits()
    {
        var tabs = new EuroOfficeNativeTabRegistry();
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        Guid firstSession = Guid.NewGuid(), secondSession = Guid.NewGuid();
        tabs.Register(first, firstSession, "document-A");
        tabs.Register(second, secondSession, "document-B");
        tabs.ApplyReceipt(first, firstSession, new EuroOfficeNativeReceipt(
            true, true, true, false, false));
        var a = tabs.Get(first);
        var b = tabs.Get(second);
        Assert.AreEqual(EuroOfficeNativeDocumentStatus.ViewLoadedUnverified, a.Status);
        Assert.AreEqual(EuroOfficeNativeDocumentStatus.Requested, b.Status);
        Assert.IsTrue(a.ModifiedObserved);
        Assert.IsFalse(b.ModifiedObserved);
        Assert.IsFalse(a.DocumentVerified);
        Assert.IsFalse(a.SaveCompleted);
        Assert.IsFalse(a.PdfReady);
    }

    [TestMethod]
    public void StaleHostAndCrossTabReceiptsAreRejected()
    {
        var tabs = new EuroOfficeNativeTabRegistry();
        Guid tab = Guid.NewGuid(), active = Guid.NewGuid(), stale = Guid.NewGuid();
        tabs.Register(tab, active, "doc");
        var receipt = new EuroOfficeNativeReceipt(true, false, false, false, false);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            tabs.ApplyReceipt(tab, stale, receipt));
        Assert.ThrowsExactly<KeyNotFoundException>(() =>
            tabs.ApplyReceipt(Guid.NewGuid(), active, receipt));
    }

    [TestMethod]
    public void CrashAndRestartIncrementGenerationWithoutReusingOldSession()
    {
        var tabs = new EuroOfficeNativeTabRegistry();
        Guid tab = Guid.NewGuid(), oldSession = Guid.NewGuid(), next = Guid.NewGuid();
        tabs.Register(tab, oldSession, "doc");
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            tabs.Restart(tab, oldSession, next));
        tabs.HostCrashed(tab, oldSession);
        Assert.AreEqual(EuroOfficeNativeDocumentStatus.Failed, tabs.Get(tab).Status);
        tabs.Restart(tab, oldSession, next);
        var snapshot = tabs.Get(tab);
        Assert.AreEqual(next, snapshot.SessionId);
        Assert.AreEqual(2, snapshot.Generation);
        Assert.AreEqual(EuroOfficeNativeDocumentStatus.Requested, snapshot.Status);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            tabs.ApplyReceipt(tab, oldSession,
                new EuroOfficeNativeReceipt(true, false, false, false, false)));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            tabs.Restart(tab, next, next));
    }

    [TestMethod]
    public void ClosedTabRejectsEventsAndCannotBeAccidentallyReRegistered()
    {
        var tabs = new EuroOfficeNativeTabRegistry();
        Guid tab = Guid.NewGuid(), session = Guid.NewGuid();
        tabs.Register(tab, session, "doc");
        tabs.Close(tab, session);
        Assert.AreEqual(EuroOfficeNativeDocumentStatus.Closed, tabs.Get(tab).Status);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            tabs.Register(tab, Guid.NewGuid(), "another-doc"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            tabs.ApplyReceipt(tab, session,
                new EuroOfficeNativeReceipt(true, false, false, false, false)));
    }

    [TestMethod]
    public void RemovingFailedOrClosedRegistryEntryRejectsStaleSession()
    {
        var registry = new EuroOfficeNativeTabRegistry();
        Guid id = Guid.NewGuid(), session = Guid.NewGuid();
        registry.Register(id, session, "synthetic");
        registry.HostCrashed(id, session);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            registry.Remove(id, Guid.NewGuid()));
        registry.Remove(id, session);
        Assert.ThrowsExactly<KeyNotFoundException>(() => registry.Get(id));
        Assert.ThrowsExactly<KeyNotFoundException>(() => registry.Remove(id, session));
    }

    [TestMethod]
    public void IntegrationBoundariesStayDisabledWithoutApprovedServices()
    {
        var integrations = new EuroOfficeNativeExternalGates();
        Assert.IsFalse(integrations.Gup2RevisionPublishAvailable);
        Assert.IsFalse(integrations.GetPrintWizardTransferAvailable);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            integrations.PublishRevisionToGup2Async(CancellationToken.None));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            integrations.TransferPdfToGetPrintWizardAsync(CancellationToken.None));
    }
}
