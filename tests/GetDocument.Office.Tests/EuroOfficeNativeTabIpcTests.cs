using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeTabIpcTests
{
    private static readonly Guid Session = Guid.Parse("25fe7878-23da-4d76-b28d-2fd5ffb187e8");
    private static readonly Guid Tab = Guid.Parse("b70b9c8b-7341-4d99-ae3b-2db31d9c25c7");

    private static string Reply(string command, long sequence, string state,
        Guid? session = null, Guid? tab = null) =>
        JsonSerializer.Serialize(new SortedDictionary<string, object>
        {
            ["command"] = command,
            ["protocol"] = EuroOfficeNativeTabIpc.Protocol,
            ["sequence"] = sequence,
            ["sessionId"] = (session ?? Session).ToString("N"),
            ["state"] = state,
            ["tabId"] = (tab ?? Tab).ToString("N")
        });

    private static string HelloReply(Guid? session = null) =>
        JsonSerializer.Serialize(new SortedDictionary<string, object>
        {
            ["command"] = "hello-ack",
            ["features"] = Array.Empty<string>(),
            ["protocol"] = EuroOfficeNativeTabIpc.Protocol,
            ["sequence"] = 0,
            ["sessionId"] = (session ?? Session).ToString("N"),
            ["state"] = "ready"
        });

    [TestMethod]
    public async Task BoundedIpcConnectAttachResizeCloseWorksWithoutOfficePermissions()
    {
        string replies = string.Join("\n", new[]
        {
            HelloReply(), Reply("ack", 1, "attached"),
            Reply("ack", 2, "resized"), Reply("ack", 3, "closed")
        }) + "\n";
        using var input = new StringReader(replies);
        using var output = new StringWriter();
        await using var channel = new EuroOfficeNativeTabIpc(input, output, Session, Tab);
        await channel.ConnectAsync(CancellationToken.None);
        await channel.AttachTabAsync(CancellationToken.None);
        await channel.ResizeAsync(1280, 720, 1.5, CancellationToken.None);
        await channel.CloseAsync(CancellationToken.None);

        Assert.AreEqual(EuroOfficeNativeTabIpcState.Closed, channel.State);
        string[] requests = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(4, requests.Length);
        using var hello = JsonDocument.Parse(requests[0]);
        Assert.AreEqual("hello", hello.RootElement.GetProperty("command").GetString());
        Assert.AreEqual(EuroOfficeDesktopProbeProtocol.PinnedUpstreamCommit,
            hello.RootElement.GetProperty("upstreamCommit").GetString());
        using var attach = JsonDocument.Parse(requests[1]);
        Assert.AreEqual("attach-tab", attach.RootElement.GetProperty("command").GetString());
        using var resize = JsonDocument.Parse(requests[2]);
        Assert.AreEqual(1280, resize.RootElement.GetProperty("width").GetInt32());
        Assert.AreEqual(1.5, resize.RootElement.GetProperty("scale").GetDouble());
        using var close = JsonDocument.Parse(requests[3]);
        Assert.AreEqual("close", close.RootElement.GetProperty("command").GetString());
        Assert.IsFalse(output.ToString().Contains("source", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(output.ToString().Contains("save-as-new-revision", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RejectsForgedSessionOrTabOrOutOfOrderSequence()
    {
        foreach (string forged in new[]
        {
            Reply("ack", 1, "attached", session: Guid.NewGuid()),
            Reply("ack", 1, "attached", tab: Guid.NewGuid()),
            Reply("ack", 2, "attached")
        })
        {
            using var input = new StringReader(HelloReply() + "\n" + forged + "\n");
            using var output = new StringWriter();
            await using var channel = new EuroOfficeNativeTabIpc(input, output, Session, Tab);
            await channel.ConnectAsync(CancellationToken.None);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                channel.AttachTabAsync(CancellationToken.None));
            Assert.AreEqual(EuroOfficeNativeTabIpcState.Failed, channel.State);
        }
    }

    [TestMethod]
    public async Task HelloFeaturesAreAlwaysEmptyAndNeverGrantFileAccess()
    {
        var invalidHello = HelloReply().Replace("\"features\":[]", "\"features\":[\"open-local\"]");
        using var input = new StringReader(invalidHello + "\n");
        using var output = new StringWriter();
        await using var channel = new EuroOfficeNativeTabIpc(input, output, Session, Tab);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            channel.ConnectAsync(CancellationToken.None));
        Assert.AreEqual(EuroOfficeNativeTabIpcState.Failed, channel.State);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            channel.AuthorizeSaveOrPrint());
    }

    [TestMethod]
    public async Task RejectsDuplicateFieldsOversizedResponsesAndEof()
    {
        foreach (string invalid in new[]
        {
            HelloReply().Replace("\"state\":", "\"state\":\"ready\",\"state\":"),
            new string('x', 8192),
            "{",
            ""
        })
        {
            using var input = new StringReader(invalid + (invalid.Length > 0 ? "\n" : ""));
            using var output = new StringWriter();
            await using var channel = new EuroOfficeNativeTabIpc(input, output, Session, Tab);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                channel.ConnectAsync(CancellationToken.None));
            Assert.AreEqual(EuroOfficeNativeTabIpcState.Failed, channel.State);
        }
    }

    [TestMethod]
    public async Task ClosePreventsReAttachAndUnverifiedResize()
    {
        using var input = new StringReader(HelloReply() + "\n");
        using var output = new StringWriter();
        await using var channel = new EuroOfficeNativeTabIpc(input, output, Session, Tab);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            channel.AttachTabAsync(CancellationToken.None));
        await channel.ConnectAsync(CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            channel.ResizeAsync(800, 600, 1.0, CancellationToken.None));
    }

    [TestMethod]
    public async Task CancellationIsFailClosed()
    {
        using var input = new StringReader(HelloReply() + "\n");
        using var output = new StringWriter();
        await using var channel = new EuroOfficeNativeTabIpc(input, output, Session, Tab);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            channel.ConnectAsync(cts.Token));
        Assert.AreEqual(EuroOfficeNativeTabIpcState.Failed, channel.State);
    }

    [TestMethod]
    public void PinnedLocalHostCanOnlyLaunchIpcProbeWithNoShell()
    {
        string root = Path.Combine(Path.GetTempPath(), "gd31j8-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string executable = Path.Combine(root, OperatingSystem.IsWindows() ? "qt-probe.exe" : "qt-probe");
            File.WriteAllBytes(executable, [0x47, 0x45, 0x54, 0x38]);
            string hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(executable)));
            var policy = new EuroOfficeNativeLabHostPolicy(root, executable, hash);
            var info = policy.CreateIpcStartInfo();
            Assert.IsFalse(info.UseShellExecute);
            Assert.IsTrue(info.RedirectStandardInput);
            Assert.IsTrue(info.RedirectStandardOutput);
            CollectionAssert.AreEqual(new[] { "--ipc-probe" }, info.ArgumentList.ToArray());
            Assert.AreEqual(Path.GetFullPath(executable), info.FileName);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [Timeout(45_000, CooperativeCancellation = true)]
    public async Task OptionalPinnedNativeProcessIpcRoundtrip()
    {
        string? binary = Environment.GetEnvironmentVariable("GD31J8_QT_IPC_BINARY");
        string? sha = Environment.GetEnvironmentVariable("GD31J8_QT_IPC_SHA256");
        if (string.IsNullOrWhiteSpace(binary) || string.IsNullOrWhiteSpace(sha))
            Assert.Inconclusive("Требуется локально собранный и заранее разрешённый Qt IPC host.");
        var policy = new EuroOfficeNativeLabHostPolicy(
            Path.GetDirectoryName(Path.GetFullPath(binary!))!, binary!, sha!);
        await using var host = await EuroOfficeNativeIpcProcessSession.StartAsync(
            policy, Session, Tab, labApproved: true, CancellationToken.None);
        await host.AttachAsync(CancellationToken.None);
        await host.ResizeAsync(1100, 700, 1.0, CancellationToken.None);
        await host.CloseAsync(CancellationToken.None);
        Assert.AreEqual(EuroOfficeNativeTabIpcState.Closed, host.State);
    }

    [TestMethod]
    public void RejectsMissingSessionOrTab()
    {
        using var input = new StringReader("");
        using var output = new StringWriter();
        Assert.ThrowsExactly<ArgumentException>(() =>
            new EuroOfficeNativeTabIpc(input, output, Guid.Empty, Tab));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new EuroOfficeNativeTabIpc(input, output, Session, Guid.Empty));
    }
}
