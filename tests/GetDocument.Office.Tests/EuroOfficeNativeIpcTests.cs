using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeIpcTests
{
    private const string Session = "e38a9a2f-9b21-4dad-8f20-28ad39d0b31f";
    private static string Frame(string eventName = "view-loaded", int code = 10,
        long sequence = 1, string session = Session) =>
        JsonSerializer.Serialize(new
        {
            protocol = EuroOfficeNativeIpc.Protocol,
            kind = "event",
            sessionId = session,
            sequence,
            @event = eventName,
            eventCode = code
        });

    [TestMethod]
    public void ParsesOnlyAllowlistedNativeEvents()
    {
        var message = EuroOfficeNativeIpc.Parse(Frame("on-save", 3002, 2));
        Assert.AreEqual(Session, message.SessionId);
        Assert.AreEqual(2L, message.Sequence);
        Assert.AreEqual("on-save", message.EventName);
        Assert.AreEqual(3002, message.EventCode);
        Assert.IsNull(message.Receipt);
        Assert.AreEqual("event", message.Kind);
    }

    [TestMethod]
    public void RejectsUntrustedCommandsVersionsAndAdditionalFields()
    {
        var valid = Frame();
        foreach (var corrupted in new[]
        {
            valid.Replace("\"event\":\"view-loaded\"", "\"event\":\"execute-command\""),
            valid.Replace("\"eventCode\":10", "\"eventCode\":5015"),
            valid.Replace("\"protocol\":", "\"unknown\":true,\"protocol\":"),
            valid.Replace("\"kind\":\"event\"", "\"kind\":\"open-local\""),
            valid.Replace("\"sessionId\":", "\"sessionId\":\"wrong\",\"sessionId\":"),
            valid.Replace(EuroOfficeNativeIpc.Protocol, "getdoc-eurodesktop-ipc/2"),
            valid.Replace("\"sequence\":1", "\"sequence\":0"),
            "{",
            new string('x', 16 * 1024)
        })
            Assert.ThrowsExactly<InvalidDataException>(() => EuroOfficeNativeIpc.Parse(corrupted));
    }

    [TestMethod]
    public async Task ReadsOneBoundedJsonLineWithoutConsumingFollowingFrame()
    {
        string wire = Frame() + "\n" + Frame("closed", 2001, 2) + "\n";
        using var input = new StringReader(wire);
        var first = await EuroOfficeNativeIpc.ReadFrameAsync(input, CancellationToken.None);
        var second = await EuroOfficeNativeIpc.ReadFrameAsync(input, CancellationToken.None);
        Assert.AreEqual(1L, first.Sequence);
        Assert.AreEqual(2L, second.Sequence);
        Assert.AreEqual("closed", second.EventName);
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(async () =>
            await EuroOfficeNativeIpc.ReadFrameAsync(input, CancellationToken.None));
    }

    [TestMethod]
    public async Task UnterminatedOrOversizeReplyNeverBecomesAValidEvent()
    {
        using var partial = new StringReader(Frame());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await EuroOfficeNativeIpc.ReadFrameAsync(partial, CancellationToken.None));
        using var longInput = new StringReader(new string('x', 8193) + "\n");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await EuroOfficeNativeIpc.ReadFrameAsync(longInput, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await EuroOfficeNativeIpc.ReadFrameAsync(new StringReader(Frame() + "\n"),
                cancellation.Token));
    }

    [TestMethod]
    public void SnapshotIsOnlyAnUnverifiedDiagnosticsReceipt()
    {
        string receipt = JsonSerializer.Serialize(new
        {
            schema = "GD31J7_EVENTS_ONLY",
            nativeEvents = new
            {
                viewLoaded = true, modifiedNotification = true,
                onSaveNotification = true, onSaveCancelled = false,
                printEndNotification = true
            },
            documentVerified = false, saveVerified = false, pdfVerified = false,
            printApproved = false, laboratoryOnly = true
        });
        using var embedded = JsonDocument.Parse(receipt);
        string wire = JsonSerializer.Serialize(new
        {
            protocol = EuroOfficeNativeIpc.Protocol, kind = "snapshot",
            sessionId = Session, sequence = 1, receipt = embedded.RootElement
        });
        var message = EuroOfficeNativeIpc.Parse(wire);
        Assert.IsNotNull(message.Receipt);
        Assert.IsTrue(message.Receipt.SaveNotificationObserved);
        Assert.IsFalse(message.Receipt.SaveCompleted);
        Assert.IsFalse(message.Receipt.PdfReady);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeIpc.Parse(wire.Replace("\"saveVerified\":false",
                                                 "\"saveVerified\":true")));
    }
}
