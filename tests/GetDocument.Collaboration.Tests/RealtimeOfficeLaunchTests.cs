using System.Text.Json;
using GetDocument.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

[TestClass]
public sealed class RealtimeOfficeLaunchTests
{
    private static readonly byte[] Key = Enumerable.Repeat((byte)49, 32).ToArray();
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 11, 0, 0, TimeSpan.Zero);
    private const string Discovery = """
        <wopi-discovery><net-zone name="external-https">
        <app name="writer">
          <action name="view" ext="docx" urlsrc="https://office.get.test/browser/view?"/>
          <action name="edit" ext="docx" urlsrc="https://office.get.test/browser/edit?"/>
        </app>
        </net-zone></wopi-discovery>
        """;

    [TestMethod]
    public void CollaboraSelectsEditActionForAuthorizedRealtimeSession()
    {
        var doc = new WopiDocument("document-42", "revision-1", "Test.docx",
            "owner", "alice", "Alice", 100, true, false);
        var ticket = RealtimeCoauthoring.Issue(doc, Key, Now,
            TimeSpan.FromMinutes(10), editingGatewayReady: true);
        var launch = WopiRealtimeLaunch.Build(Discovery, ".docx",
            new Uri("https://office.get.test/"),
            new Uri("https://files.get.test/wopi/files/document-42"),
            new Uri("https://files.get.test/"), ticket);
        Assert.AreEqual("/browser/edit", launch.EditorUrl.AbsolutePath);
        Assert.AreEqual("https://files.get.test/wopi/files/document-42",
            launch.WopiSource.AbsoluteUri);
        Assert.AreEqual(ticket.AccessToken, launch.AccessToken);
        Assert.IsFalse(launch.EditorUrl.AbsoluteUri.Contains("access_token=", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(launch.EditorUrl.Query.Contains("WOPISrc=", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SameDocumentUsesSameWopiSourceButDifferentUserTokens()
    {
        var alice = new WopiDocument("doc", "r1", "Test.docx",
            "owner", "alice", "Alice", 100, true, false);
        var bob = alice with { UserId = "bob", UserFriendlyName = "Bob" };
        var a = RealtimeCoauthoring.Issue(alice, Key, Now, TimeSpan.FromMinutes(5), true);
        var b = RealtimeCoauthoring.Issue(bob, Key, Now, TimeSpan.FromMinutes(5), true);
        var wopiSrc = new Uri("https://files.get.test/wopi/files/doc");
        var l1 = WopiRealtimeLaunch.Build(Discovery, "docx",
            new Uri("https://office.get.test/"), wopiSrc, new Uri("https://files.get.test/"), a);
        var l2 = WopiRealtimeLaunch.Build(Discovery, "docx",
            new Uri("https://office.get.test/"), wopiSrc, new Uri("https://files.get.test/"), b);
        Assert.AreEqual(l1.EditorUrl, l2.EditorUrl);
        Assert.AreNotEqual(l1.AccessToken, l2.AccessToken);
    }

    [TestMethod]
    public void UnreadyGatewayForcesViewAction()
    {
        var doc = new WopiDocument("doc", "r1", "Test.docx",
            "owner", "alice", "Alice", 100, true, false);
        var ticket = RealtimeCoauthoring.Issue(doc, Key, Now, TimeSpan.FromMinutes(5), false);
        var launch = WopiRealtimeLaunch.Build(Discovery, "docx",
            new Uri("https://office.get.test/"),
            new Uri("https://files.get.test/wopi/files/doc"),
            new Uri("https://files.get.test/"), ticket);
        Assert.AreEqual("/browser/view", launch.EditorUrl.AbsolutePath);
    }

    [TestMethod]
    public void RejectsUnapprovedWopiSource()
    {
        var doc = new WopiDocument("doc", "r1", "Test.docx",
            "owner", "alice", "Alice", 100, true, false);
        var ticket = RealtimeCoauthoring.Issue(doc, Key, Now, TimeSpan.FromMinutes(5), true);
        Assert.ThrowsExactly<ArgumentException>(() => WopiRealtimeLaunch.Build(
            Discovery, "docx", new Uri("https://office.get.test/"),
            new Uri("https://evil.example/wopi/files/doc"),
            new Uri("https://files.get.test/"), ticket));
    }

    [TestMethod]
    public void CannotAttachAuthorizedTicketToAnotherWopiDocumentPath()
    {
        var doc = new WopiDocument("doc-one", "r1", "Test.docx",
            "owner", "alice", "Alice", 100, true, false);
        var ticket = RealtimeCoauthoring.Issue(doc, Key, Now, TimeSpan.FromMinutes(5), true);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => WopiRealtimeLaunch.Build(
            Discovery, "docx", new Uri("https://office.get.test/"),
            new Uri("https://files.get.test/wopi/files/doc-two"),
            new Uri("https://files.get.test/"), ticket));
    }

    [TestMethod]
    public void OnlyOfficeFastModeDoesNotPermitStrictSwitch()
    {
        var opts = new OnlyOfficeSessionOptions("doc", "r1", "Text.docx",
            "alice", "Alice",
            new Uri("https://files.get.test/"),
            new Uri("https://files.get.test/session/doc/content"),
            new Uri("https://files.get.test/session/doc/callback"),
            new Uri("https://office.get.test/"),
            true, false);
        using var json = JsonDocument.Parse(OnlyOfficeSessionFactory.Create(opts, Key).ConfigurationJson);
        var editor = json.RootElement.GetProperty("editorConfig");
        var collaboration = editor.GetProperty("coEditing");
        Assert.AreEqual("fast", collaboration.GetProperty("mode").GetString());
        Assert.IsFalse(collaboration.GetProperty("change").GetBoolean());
    }
}
