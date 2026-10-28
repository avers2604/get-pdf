using System.Security.Cryptography;
using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeOsrStreamTests
{
    internal static readonly Guid Session = Guid.Parse("25fe7878-23da-4d76-b28d-2fd5ffb187e8");
    internal static readonly Guid Tab = Guid.Parse("b70b9c8b-7341-4d99-ae3b-2db31d9c25c7");
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/c8sAAAAASUVORK5CYII=");

    private static string Frame(int number, Guid? session = null, Guid? tab = null,
        string? hash = null, string source = "cef-osr-wayland-synthetic-only")
        => JsonSerializer.Serialize(new SortedDictionary<string, object>
        {
            ["command"] = "frame",
            ["documentVerified"] = false,
            ["frameNumber"] = number,
            ["laboratoryOnly"] = true,
            ["payloadBase64"] = Convert.ToBase64String(Png),
            ["pdfVerified"] = false,
            ["pixelHeight"] = 1,
            ["pixelWidth"] = 1,
            ["printApproved"] = false,
            ["protocol"] = EuroOfficeOsrStreamFrame.Protocol,
            ["saveVerified"] = false,
            ["sessionId"] = (session ?? Session).ToString("N"),
            ["sha256"] = hash ?? Convert.ToHexString(SHA256.HashData(Png)).ToLowerInvariant(),
            ["source"] = source,
            ["tabId"] = (tab ?? Tab).ToString("N")
        });

    private static string End(int count, Guid? session = null) =>
        JsonSerializer.Serialize(new SortedDictionary<string, object>
        {
            ["command"] = "end",
            ["framesProduced"] = count,
            ["laboratoryOnly"] = true,
            ["protocol"] = EuroOfficeOsrStreamFrame.Protocol,
            ["reason"] = "time-limit",
            ["sessionId"] = (session ?? Session).ToString("N"),
            ["tabId"] = Tab.ToString("N")
        });

    [TestMethod]
    public async Task ReceivesSequentialActualCefLabFramesWithoutPublishingThem()
    {
        using var incoming = new StringReader(Frame(1) + "\n" + Frame(2) + "\n" + End(2) + "\n");
        var shown = new List<EuroOfficeOsrStreamFrame>();
        var receiver = new EuroOfficeOsrStreamReader(Session, Tab);
        var count = await receiver.ReadAsync(incoming, frame => {
            shown.Add(frame);
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.AreEqual(2, count);
        Assert.AreEqual(2, shown.Count);
        Assert.AreEqual(2, shown[1].FrameNumber);
        Assert.IsTrue(shown.All(x => x.CefPaintObserved));
        Assert.IsTrue(shown.All(x => !x.OfficeDocumentVerified && !x.PrintApproved));
    }

    [TestMethod]
    public async Task RejectsSessionMismatchReplaysAndMissingTerminalReceipt()
    {
        foreach (string payload in new[] {
            Frame(1, session: Guid.NewGuid()) + "\n" + End(1),
            Frame(1) + "\n" + Frame(1) + "\n" + End(2),
            Frame(2) + "\n" + End(1),
            Frame(1) + "\n" + End(0),
            Frame(1) + "\n",
            End(1) + "\n",
            Frame(1) + "\n" + End(1, session: Guid.NewGuid()),
            Frame(1) + "\n" + End(1) + "\n" + "trailing-data"
        })
        {
            using var incoming = new StringReader(payload);
            var receiver = new EuroOfficeOsrStreamReader(Session, Tab);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                receiver.ReadAsync(incoming, _ => Task.CompletedTask, CancellationToken.None));
        }
    }

    [TestMethod]
    public void RejectsForgedSourceDigestOversizeAndSuccessFlags()
    {
        string valid = Frame(1);
        foreach (var bad in new[] {
            Frame(1, source: "qt-painter-test-only"),
            Frame(1, hash: new string('0', 64)),
            Frame(1, tab: Guid.NewGuid()),
            valid.Replace("\"saveVerified\":false", "\"saveVerified\":true"),
            valid.Replace("\"laboratoryOnly\":true", "\"laboratoryOnly\":false"),
            valid.Replace("\"command\":", "\"command\":\"frame\",\"command\":"),
            valid.Replace("\"pixelHeight\":1", "\"pixelHeight\":7000"),
            valid.Replace("\"pixelWidth\":1", "\"other\":true,\"pixelWidth\":1"),
            new string('x', 70_000)
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                EuroOfficeOsrStreamFrame.Parse(bad, Session, Tab, 1));
    }

    [TestMethod]
    public async Task EmptyStreamCanFinishCleanlyWithZeroFrames()
    {
        using var incoming = new StringReader(End(0) + "\n");
        var receiver = new EuroOfficeOsrStreamReader(Session, Tab);
        int count = await receiver.ReadAsync(incoming,
            _ => throw new AssertFailedException("No frames expected"), CancellationToken.None);
        Assert.AreEqual(0, count);
    }

    [TestMethod]
    public void HashValidatedBytesAreImmutable()
    {
        var frame = EuroOfficeOsrStreamFrame.Parse(Frame(1), Session, Tab, 1);
        var copy = frame.PngBytes;
        copy[0] = 0;
        CollectionAssert.AreEqual(Png, frame.PngBytes);
    }

    [TestMethod]
    public async Task DelayedOrCancelledConsumerCannotContinueReading()
    {
        using var incoming = new StringReader(Frame(1) + "\n" + End(1) + "\n");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var receiver = new EuroOfficeOsrStreamReader(Session, Tab);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            receiver.ReadAsync(incoming, _ => Task.CompletedTask, cancelled.Token));
    }
}
