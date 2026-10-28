using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeOsrLabKeyboardTests
{
    private static readonly Guid Session = Guid.NewGuid();
    private static readonly Guid Tab = Guid.NewGuid();

    [TestMethod]
    public async Task KeyboardRequiresSeparatePermissionEvenWhenClicksEnabled()
    {
        using var sink = new StringWriter();
        await using var input = new EuroOfficeOsrLabInputChannel(sink, Session, Tab, true,
            clickLabApproved: true);
        await input.SendFocusAsync(true, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            input.SendNavigationKeyAsync("ArrowLeft", false, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            input.SendTextCharacterAsync('Я', CancellationToken.None));
        Assert.ThrowsExactly<NotSupportedException>(() => input.SendKeyboardOrIme());
        Assert.ThrowsExactly<NotSupportedException>(() => input.ShareClipboard());
    }

    [TestMethod]
    public async Task NavigationAndTextAreStrictlySeparatedWithSequenceAndSession()
    {
        using var sink = new StringWriter();
        await using var input = new EuroOfficeOsrLabInputChannel(sink, Session, Tab, true,
            clickLabApproved: true, keyboardLabApproved: true);
        await input.SendFocusAsync(true, CancellationToken.None);
        await input.SendNavigationKeyAsync("ArrowLeft", true, CancellationToken.None);
        await input.SendTextCharacterAsync('Я', CancellationToken.None);
        await input.SendTextCharacterAsync('A', CancellationToken.None);
        var lines = sink.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(4, lines.Length);
        var commands = new [] { "focus", "nav-key", "text-char", "text-char" };
        for (int i = 0; i < lines.Length; i++)
        {
            using var obj = JsonDocument.Parse(lines[i]);
            Assert.AreEqual(commands[i], obj.RootElement.GetProperty("command").GetString());
            Assert.AreEqual(i + 1, obj.RootElement.GetProperty("sequence").GetInt32());
            Assert.AreEqual(Session.ToString("N"), obj.RootElement.GetProperty("sessionId").GetString());
            Assert.AreEqual(Tab.ToString("N"), obj.RootElement.GetProperty("tabId").GetString());
        }
        using var key = JsonDocument.Parse(lines[1]);
        Assert.AreEqual("ArrowLeft", key.RootElement.GetProperty("key").GetString());
        Assert.IsTrue(key.RootElement.GetProperty("shift").GetBoolean());
        using var text = JsonDocument.Parse(lines[2]);
        Assert.AreEqual((int)'Я', text.RootElement.GetProperty("codePoint").GetInt32());
    }

    [TestMethod]
    public async Task ReservedShortcutsAreNotNavigationCommands()
    {
        using var sink = new StringWriter();
        await using var input = new EuroOfficeOsrLabInputChannel(sink, Session, Tab, true,
            clickLabApproved: true, keyboardLabApproved: true);
        await input.SendFocusAsync(true, CancellationToken.None);
        foreach (string key in new[] {
            "Ctrl+Tab", "Ctrl+W", "Ctrl+P", "Alt+F4", "Meta", "F5", "F12",
            "BrowserBack", "Space", "a", "Insert", "PrintScreen", "ContextMenu"
        })
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                input.SendNavigationKeyAsync(key, false, CancellationToken.None));
        Assert.AreEqual(1, sink.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [TestMethod]
    public async Task TextCommitsAreOneNonControlBmpScalarOnly()
    {
        using var sink = new StringWriter();
        await using var input = new EuroOfficeOsrLabInputChannel(sink, Session, Tab, true,
            clickLabApproved: true, keyboardLabApproved: true);
        await input.SendFocusAsync(true, CancellationToken.None);
        foreach (char c in new [] { '\0', '\n', '\r', '\t', '\u001b', '\u007f',
                                    '\ud800', '\udfff', '\u200e', '\ufeff' })
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
                input.SendTextCharacterAsync(c, CancellationToken.None));
        await input.SendTextCharacterAsync('ё', CancellationToken.None);
        await input.SendTextCharacterAsync('7', CancellationToken.None);
        await input.SendTextCharacterAsync('!', CancellationToken.None);
        Assert.AreEqual(4, sink.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.ThrowsExactly<NotSupportedException>(() => input.SendImeComposition());
    }

    [TestMethod]
    public async Task RequiresFocusAndReleasedPointerForKeyboard()
    {
        using var sink = new StringWriter();
        await using var input = new EuroOfficeOsrLabInputChannel(sink, Session, Tab, true,
            clickLabApproved: true, keyboardLabApproved: true);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            input.SendNavigationKeyAsync("ArrowDown", false, CancellationToken.None));
        await input.SendFocusAsync(true, CancellationToken.None);
        await input.SendLeftButtonDownAsync(.4, .4, 1, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            input.SendTextCharacterAsync('T', CancellationToken.None));
        await input.SendLeftButtonUpAsync(.4, .4, CancellationToken.None);
        await input.SendNavigationKeyAsync("ArrowDown", false, CancellationToken.None);
    }
}
