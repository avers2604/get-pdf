using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeIpcPumpTests
{
    private const string Session = "e38a9a2f-9b21-4dad-8f20-28ad39d0b31f";

    private static string Frame(string kind, string eventName, int eventCode, int sequence) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            protocol = EuroOfficeNativeIpc.Protocol,
            kind, sessionId = Session, sequence,
            @event = eventName, eventCode
        }) + "\n";

    [TestMethod]
    public async Task ClosedNativeStreamReturnsTerminalTabState()
    {
        var registry = new EuroOfficeNativeTabRegistry();
        registry.Register("tab-a", "doc-a", Session);
        using var source = new StringReader(Frame("event", "view-loaded", 10, 1) +
                                            Frame("event", "closed", 2001, 2));
        var pump = new EuroOfficeNativeIpcPump(registry);
        await pump.ConsumeAsync("tab-a", source, CancellationToken.None);
        Assert.AreEqual(EuroOfficeNativeDocumentStatus.Closed, registry.Snapshot("tab-a").Status);
        Assert.AreEqual(2L, registry.Snapshot("tab-a").LastSequence);
    }

    [TestMethod]
    public async Task UnexpectedEofMustFailAnActiveTab()
    {
        var registry = new EuroOfficeNativeTabRegistry();
        registry.Register("tab-a", "doc-a", Session);
        var pump = new EuroOfficeNativeIpcPump(registry);
        using var source = new StringReader(Frame("event", "view-loaded", 10, 1));
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() =>
            pump.ConsumeAsync("tab-a", source, CancellationToken.None));
        Assert.AreEqual(EuroOfficeNativeDocumentStatus.Failed, registry.Snapshot("tab-a").Status);
    }

    [TestMethod]
    public async Task ForgedIpcEventAbortsOnlyItsOwnTab()
    {
        var registry = new EuroOfficeNativeTabRegistry();
        registry.Register("tab-a", "doc-a", Session);
        var secondSession = Guid.NewGuid().ToString("D");
        registry.Register("tab-b", "doc-b", secondSession);
        var pump = new EuroOfficeNativeIpcPump(registry);
        using var source = new StringReader(Frame("event", "view-loaded", 10, 1) +
                                            Frame("event", "on-save", 999, 2));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            pump.ConsumeAsync("tab-a", source, CancellationToken.None));
        Assert.AreEqual(EuroOfficeNativeDocumentStatus.Failed, registry.Snapshot("tab-a").Status);
        Assert.AreEqual(EuroOfficeNativeDocumentStatus.Requested, registry.Snapshot("tab-b").Status);
    }

    [TestMethod]
    public async Task CancelledTransportDoesNotLeaveTabActive()
    {
        var registry = new EuroOfficeNativeTabRegistry();
        registry.Register("tab-a", "doc-a", Session);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var pump = new EuroOfficeNativeIpcPump(registry);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            pump.ConsumeAsync("tab-a", new StringReader(""), cancel.Token));
        Assert.AreEqual(EuroOfficeNativeDocumentStatus.Failed, registry.Snapshot("tab-a").Status);
    }
}
