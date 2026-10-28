using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeOsrLabInputChannelTests
{
    private static readonly Guid Session = Guid.Parse("25fe7878-23da-4d76-b28d-2fd5ffb187e8");
    private static readonly Guid Tab = Guid.Parse("b70b9c8b-7341-4d99-ae3b-2db31d9c25c7");

    [TestMethod]
    public async Task PointerMoveScrollAndFocusUseBoundedOrderedSessionMessages()
    {
        using var output = new StringWriter();
        await using var input = new EuroOfficeOsrLabInputChannel(
            output, Session, Tab, laboratoryApproved: true);
        await input.SendPointerMoveAsync(0.25, 0.75, CancellationToken.None);
        await input.SendWheelAsync(0.5, 0.5, 0, -120, CancellationToken.None);
        await input.SendFocusAsync(true, CancellationToken.None);
        await input.SendFocusAsync(false, CancellationToken.None);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(4, lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            using var frame = JsonDocument.Parse(lines[i]);
            var root = frame.RootElement;
            Assert.AreEqual("getdoc-cef-osr-input/1", root.GetProperty("protocol").GetString());
            Assert.AreEqual(Session.ToString("N"), root.GetProperty("sessionId").GetString());
            Assert.AreEqual(Tab.ToString("N"), root.GetProperty("tabId").GetString());
            Assert.AreEqual(i + 1, root.GetProperty("sequence").GetInt32());
            Assert.IsTrue(lines[i].Length < 4096);
        }
        using var move = JsonDocument.Parse(lines[0]);
        Assert.AreEqual("move", move.RootElement.GetProperty("command").GetString());
        Assert.AreEqual(2500, move.RootElement.GetProperty("x10000").GetInt32());
        Assert.AreEqual(7500, move.RootElement.GetProperty("y10000").GetInt32());
        using var wheel = JsonDocument.Parse(lines[1]);
        Assert.AreEqual("wheel", wheel.RootElement.GetProperty("command").GetString());
        Assert.AreEqual(-120, wheel.RootElement.GetProperty("deltaY").GetInt32());
        using var focus = JsonDocument.Parse(lines[2]);
        Assert.AreEqual("focus", focus.RootElement.GetProperty("command").GetString());
        Assert.IsTrue(focus.RootElement.GetProperty("focused").GetBoolean());
    }

    [TestMethod]
    public async Task RejectedNonLabInputDoesNotWrite()
    {
        using var output = new StringWriter();
        Assert.ThrowsExactly<NotSupportedException>(() =>
            new EuroOfficeOsrLabInputChannel(output, Session, Tab, laboratoryApproved: false));
        Assert.AreEqual("", output.ToString());
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task OutOfBoundsCoordinatesAndInvalidWheelAreRejected()
    {
        using var output = new StringWriter();
        await using var input = new EuroOfficeOsrLabInputChannel(output, Session, Tab, true);
        foreach (double invalid in new [] { -0.01, 1.0, double.NaN, double.PositiveInfinity })
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
                input.SendPointerMoveAsync(invalid, 0.5, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            input.SendWheelAsync(0.5, 0.5, 2000, 0, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            input.SendWheelAsync(0.5, 0.5, 0, 0, CancellationToken.None));
        Assert.AreEqual("", output.ToString());
    }

    [TestMethod]
    public async Task CancellingOrDisposingInputPreventsFurtherMessages()
    {
        using var output = new StringWriter();
        var input = new EuroOfficeOsrLabInputChannel(output, Session, Tab, true);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            input.SendFocusAsync(true, cts.Token));
        Assert.AreEqual("", output.ToString());
        await input.DisposeAsync();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
            input.SendFocusAsync(true, CancellationToken.None));
    }

    [TestMethod]
    public async Task BrokenPipePermanentlyRevokesInputToPreventReplay()
    {
        var output = new StringWriter();
        await using var input = new EuroOfficeOsrLabInputChannel(
            output, Session, Tab, true);
        output.Dispose();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
            input.SendFocusAsync(true, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
            input.SendFocusAsync(true, CancellationToken.None));
    }

    [TestMethod]
    public async Task KeyboardClipboardClicksCannotCrossInputChannel()
    {
        using var output = new StringWriter();
        await using var input = new EuroOfficeOsrLabInputChannel(output, Session, Tab, true);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            input.SendKeyboardOrIme());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            input.SendMouseClick());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            input.ShareClipboard());
        Assert.AreEqual("", output.ToString());
    }
}
