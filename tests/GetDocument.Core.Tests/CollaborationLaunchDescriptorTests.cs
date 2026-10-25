using GetDocument.Core.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class CollaborationLaunchDescriptorTests
{
    private static readonly Uri Broker = new("https://files.get.intra/");
    private static readonly Uri Office = new("https://office.get.intra/");
    private static readonly Uri LaunchPage = new(
        "https://files.get.intra/get-document/editor/launch/aabbccddeeff00112233445566778899");

    [TestMethod]
    public void AllowsOnlyExactLaunchPageAndApprovedOfficeOrigin()
    {
        var launch = CollaborationLaunchDescriptor.Create(
            "Общий документ.docx", LaunchPage, Broker, Office);
        Assert.IsTrue(launch.IsAllowedNavigation(LaunchPage));
        Assert.IsTrue(launch.IsAllowedNavigation(
            new Uri("https://office.get.intra/browser/edit?WOPISrc=https%3A%2F%2Ffiles.get.intra%2Fwopi%2Ffiles%2F42")));
        Assert.IsTrue(launch.IsAllowedNavigation(new Uri("about:blank")));
        Assert.IsFalse(launch.IsAllowedNavigation(new Uri("https://evil.example/")));
        Assert.IsFalse(launch.IsAllowedNavigation(
            new Uri("https://files.get.intra/get-document/editor/launch/ffffffffffffffffffffffffffffffff")));
    }

    [TestMethod]
    public void RejectsAnyBearerOrCodeInLaunchUrl()
    {
        foreach (string unsafeUrl in new[]
        {
            LaunchPage.AbsoluteUri + "?access_token=secret",
            LaunchPage.AbsoluteUri + "?token=secret",
            LaunchPage.AbsoluteUri + "#token",
            "http://files.get.intra/get-document/editor/launch/aabbccddeeff00112233445566778899",
            "https://user:secret@files.get.intra/get-document/editor/launch/aabbccddeeff00112233445566778899",
            "https://files.get.intra/wopi/files/42?access_token=secret"
        })
        {
            Assert.ThrowsExactly<ArgumentException>(() =>
                CollaborationLaunchDescriptor.Create("Документ", new Uri(unsafeUrl), Broker, Office));
        }
    }

    [TestMethod]
    public void RejectsUnsignedOrInvalidNoncePaths()
    {
        foreach (string value in new[]
        {
            "https://files.get.intra/get-document/editor/launch/guessable",
            "https://files.get.intra/get-document/editor/launch/../x",
            "https://files.get.intra/not-editor/launch/aabbccddeeff00112233445566778899"
        })
        {
            Assert.ThrowsExactly<ArgumentException>(() =>
                CollaborationLaunchDescriptor.Create("Документ", new Uri(value), Broker, Office));
        }
    }

    [TestMethod]
    public void RejectsNavigationsWithTokensOrForeignOrigins()
    {
        var launch = CollaborationLaunchDescriptor.Create("Документ", LaunchPage, Broker, Office);
        Assert.IsFalse(launch.IsAllowedNavigation(
            new Uri("https://office.get.intra/browser/edit?access_token=secret")));
        Assert.IsFalse(launch.IsAllowedNavigation(
            new Uri("https://office.get.intra.attacker.example/browser/edit")));
        Assert.IsFalse(launch.IsAllowedNavigation(new Uri("file:///etc/passwd")));
        Assert.IsFalse(launch.IsAllowedNavigation(new Uri("javascript:alert(1)")));
        Assert.IsFalse(launch.IsAllowedNavigation(new Uri("data:text/html,hello")));
        Assert.IsFalse(launch.IsAllowedNavigation(new Uri("https://files.get.intra/private/")));
    }

    [TestMethod]
    public void RejectsUntrustedOrRelativeOrigins()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            CollaborationLaunchDescriptor.Create("Документ", LaunchPage,
                new Uri("http://files.get.intra/"), Office));
        Assert.ThrowsExactly<ArgumentException>(() =>
            CollaborationLaunchDescriptor.Create("Документ", LaunchPage,
                Broker, new Uri("https://office.get.intra/path")));
        Assert.ThrowsExactly<ArgumentException>(() =>
            CollaborationLaunchDescriptor.Create("Документ", LaunchPage,
                Broker, Broker));
    }

    [TestMethod]
    public void CannotOpenEmptyOrOverlongTabTitles()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            CollaborationLaunchDescriptor.Create("", LaunchPage, Broker, Office));
        Assert.ThrowsExactly<ArgumentException>(() =>
            CollaborationLaunchDescriptor.Create(new string('A', 300), LaunchPage, Broker, Office));
    }
}
