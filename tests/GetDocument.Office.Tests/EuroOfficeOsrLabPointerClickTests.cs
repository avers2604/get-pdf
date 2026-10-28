using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// Specifications only. Native CEF and .NET tests are deferred to a local
/// isolated laboratory at the user's request.
/// </summary>
[TestClass]
public sealed class EuroOfficeOsrLabPointerClickTests
{
    private static readonly Guid Session = Guid.NewGuid();
    private static readonly Guid Tab = Guid.NewGuid();

    [TestMethod]
    public async Task ClickModeNeedsSeparateApprovalAndDoesNotChangeOldInputLab()
    {
        using var output = new StringWriter();
        await using var oldMode = new EuroOfficeOsrLabInputChannel(output,
            Session, Tab, laboratoryApproved: true);
        await oldMode.SendFocusAsync(true, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            oldMode.SendLeftButtonDownAsync(.5, .5, 1, CancellationToken.None));
        Assert.ThrowsExactly<NotSupportedException>(() => oldMode.SendKeyboardOrIme());
        Assert.ThrowsExactly<NotSupportedException>(() => oldMode.ShareClipboard());
    }

    [TestMethod]
    public async Task LeftDownMoveUpAreSequentialWithExactlyPairedCoordinates()
    {
        using var output = new StringWriter();
        await using var input = new EuroOfficeOsrLabInputChannel(
            output, Session, Tab, laboratoryApproved: true,
            clickLabApproved: true);
        await input.SendFocusAsync(true, CancellationToken.None);
        await input.SendLeftButtonDownAsync(.25, .4, 1, CancellationToken.None);
        await input.SendPointerMoveAsync(.3, .6, CancellationToken.None);
        await input.SendLeftButtonUpAsync(.3, .6, CancellationToken.None);
        await input.SendFocusAsync(false, CancellationToken.None);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        CollectionAssert.AreEqual(new[] { "focus", "left-down", "move", "left-up", "focus" },
            lines.Select(x => JsonDocument.Parse(x).RootElement.GetProperty("command").GetString()).ToArray());
        for (int i = 0; i < lines.Length; i++)
        {
            using var doc = JsonDocument.Parse(lines[i]);
            Assert.AreEqual(i + 1, doc.RootElement.GetProperty("sequence").GetInt32());
            Assert.AreEqual(Session.ToString("N"), doc.RootElement.GetProperty("sessionId").GetString());
            Assert.AreEqual(Tab.ToString("N"), doc.RootElement.GetProperty("tabId").GetString());
        }
        using var down = JsonDocument.Parse(lines[1]);
        Assert.AreEqual(2500, down.RootElement.GetProperty("x10000").GetInt32());
        Assert.AreEqual(4000, down.RootElement.GetProperty("y10000").GetInt32());
        Assert.AreEqual(1, down.RootElement.GetProperty("clickCount").GetInt32());
    }

    [TestMethod]
    public async Task DownDownUpAndUpWithoutDownAreRejected()
    {
        using var output = new StringWriter();
        await using var input = new EuroOfficeOsrLabInputChannel(output,
            Session, Tab, true, clickLabApproved: true);
        await input.SendFocusAsync(true, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            input.SendLeftButtonUpAsync(.2, .2, CancellationToken.None));
        await input.SendLeftButtonDownAsync(.2, .2, 2, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            input.SendLeftButtonDownAsync(.3, .3, 1, CancellationToken.None));
        await input.SendLeftButtonUpAsync(.2, .2, CancellationToken.None);
        Assert.AreEqual(3, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [TestMethod]
    public async Task FocusLossRequiresReleasedButtonAndBlocksNewDrags()
    {
        using var output = new StringWriter();
        await using var input = new EuroOfficeOsrLabInputChannel(output,
            Session, Tab, true, clickLabApproved: true);
        await input.SendFocusAsync(true, CancellationToken.None);
        await input.SendLeftButtonDownAsync(.4, .4, 1, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            input.SendFocusAsync(false, CancellationToken.None));
        await input.SendLeftButtonUpAsync(.4, .4, CancellationToken.None);
        await input.SendFocusAsync(false, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            input.SendLeftButtonDownAsync(.4, .4, 1, CancellationToken.None));
    }

    [TestMethod]
    public async Task InvalidClickCountAndOutOfBoundsCoordinatesNeverReachWire()
    {
        using var output = new StringWriter();
        await using var input = new EuroOfficeOsrLabInputChannel(output,
            Session, Tab, true, clickLabApproved: true);
        await input.SendFocusAsync(true, CancellationToken.None);
        foreach (var count in new [] { 0, 3, 10 })
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
                input.SendLeftButtonDownAsync(.5, .5, count, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            input.SendLeftButtonDownAsync(1, .5, 1, CancellationToken.None));
        Assert.AreEqual(1, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [TestMethod]
    public async Task KeyboardAndClipboardRemainBlockedWhenClicksAreAllowed()
    {
        using var output = new StringWriter();
        await using var input = new EuroOfficeOsrLabInputChannel(output,
            Session, Tab, true, clickLabApproved: true);
        Assert.ThrowsExactly<NotSupportedException>(() => input.SendKeyboardOrIme());
        Assert.ThrowsExactly<NotSupportedException>(() => input.ShareClipboard());
        Assert.ThrowsExactly<NotSupportedException>(() => input.SendMouseClick());
        Assert.AreEqual("", output.ToString());
        await Task.CompletedTask;
    }
}
