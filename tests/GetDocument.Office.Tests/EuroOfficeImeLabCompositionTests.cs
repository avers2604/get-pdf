using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeImeLabCompositionTests
{
    private static readonly Guid Session = Guid.Parse("25fe7878-23da-4d76-b28d-2fd5ffb187e8");
    private static readonly Guid Tab = Guid.Parse("b70b9c8b-7341-4d99-ae3b-2db31d9c25c7");

    [TestMethod]
    public async Task ExplicitApprovedCompositionCommitsOnlyFinalTextNotPreedit()
    {
        using var output = new StringWriter();
        await using var channel = NewChannel(output);
        await channel.SendFocusAsync(true, CancellationToken.None);
        using var composition = new EuroOfficeImeLabComposition(Session, Tab, approved: true);
        composition.Begin();
        composition.Update("п");
        composition.Update("пр");
        composition.Update("привет");
        Assert.AreEqual(1, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        await composition.CommitAsync(channel, CancellationToken.None);
        var frames = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(2, frames.Length);
        using var frame = JsonDocument.Parse(frames[1]);
        Assert.AreEqual("ime-commit", frame.RootElement.GetProperty("command").GetString());
        Assert.AreEqual(2, frame.RootElement.GetProperty("sequence").GetInt32());
        Assert.AreEqual(Session.ToString("N"), frame.RootElement.GetProperty("sessionId").GetString());
        Assert.AreEqual(Tab.ToString("N"), frame.RootElement.GetProperty("tabId").GetString());
        CollectionAssert.AreEqual("привет".Select(x => (int)x).ToArray(),
            frame.RootElement.GetProperty("codePoints").EnumerateArray()
                .Select(x => x.GetInt32()).ToArray());
        Assert.IsFalse(composition.IsComposing);
        Assert.AreEqual(0, composition.BufferedCharacters);
    }

    [TestMethod]
    public async Task CancelClearsPreeditWithoutSendingText()
    {
        using var output = new StringWriter();
        await using var channel = NewChannel(output);
        await channel.SendFocusAsync(true, CancellationToken.None);
        using var composition = new EuroOfficeImeLabComposition(Session, Tab, approved: true);
        composition.Begin();
        composition.Update("secret");
        composition.Cancel();
        Assert.IsFalse(composition.IsComposing);
        Assert.AreEqual(0, composition.BufferedCharacters);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            composition.CommitAsync(channel, CancellationToken.None));
        Assert.AreEqual(1, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [TestMethod]
    public async Task FailsClosedOnSessionMismatchOrMissingApproval()
    {
        Assert.ThrowsExactly<NotSupportedException>(() =>
            new EuroOfficeImeLabComposition(Session, Tab, approved: false));
        using var output = new StringWriter();
        await using var other = new EuroOfficeOsrLabInputChannel(
            output, Guid.NewGuid(), Tab, true, clickLabApproved: true,
            keyboardLabApproved: true, imeCommitLabApproved: true);
        await other.SendFocusAsync(true, CancellationToken.None);
        using var composition = new EuroOfficeImeLabComposition(Session, Tab, approved: true);
        composition.Begin();
        composition.Update("ok");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            composition.CommitAsync(other, CancellationToken.None));
    }

    [TestMethod]
    public void RejectsInvalidUnicodeAndOversizedPreedit()
    {
        using var composition = new EuroOfficeImeLabComposition(Session, Tab, approved: true);
        composition.Begin();
        foreach (string text in new[] {
            "", "a\nb", "a\tb", "\ud800", "a\udfff",
            "a\u202eb", "a\u200eb", "a\u0000b", new string('x', 17)
        })
            Assert.ThrowsExactly<ArgumentException>(() => composition.Update(text));
        Assert.AreEqual(0, composition.BufferedCharacters);
    }

    [TestMethod]
    public async Task CommitsHaveBoundedAggregatePerLabSessionAndRequireSeparateApproval()
    {
        using var oldOutput = new StringWriter();
        await using var oldChannel = new EuroOfficeOsrLabInputChannel(
            oldOutput, Session, Tab, true, clickLabApproved: true,
            keyboardLabApproved: true);
        await oldChannel.SendFocusAsync(true, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            oldChannel.SendImeCommittedTextAsync("тест", CancellationToken.None));

        using var output = new StringWriter();
        await using var channel = NewChannel(output);
        await channel.SendFocusAsync(true, CancellationToken.None);
        for (int i = 0; i < 4; i++)
            await channel.SendImeCommittedTextAsync(new string('Я', 16),
                CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            channel.SendImeCommittedTextAsync("А", CancellationToken.None));
        Assert.AreEqual(5, output.ToString().Split('\n',
            StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [TestMethod]
    public async Task ClipboardAndUntypedPasteRemainForbidden()
    {
        Assert.ThrowsExactly<NotSupportedException>(
            EuroOfficeLabClipboardGate.RequireOsReadApproval);
        Assert.ThrowsExactly<NotSupportedException>(
            EuroOfficeLabClipboardGate.RequireOsWriteApproval);
        Assert.ThrowsExactly<NotSupportedException>(
            EuroOfficeLabClipboardGate.RequireHtmlOrRichTextApproval);
        using var output = new StringWriter();
        await using var channel = NewChannel(output);
        Assert.ThrowsExactly<NotSupportedException>(() => channel.ShareClipboard());
        Assert.ThrowsExactly<NotSupportedException>(() => channel.SendImeComposition());
        await Task.CompletedTask;
    }

    private static EuroOfficeOsrLabInputChannel NewChannel(TextWriter output) =>
        new(output, Session, Tab, laboratoryApproved: true,
            clickLabApproved: true, keyboardLabApproved: true, imeCommitLabApproved: true);
}
