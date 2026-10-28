using System.Security.Cryptography;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeLabHostTests
{
    [TestMethod]
    public void VerifiedHostRequiresExistingLocalExecutableWithPinnedHash()
    {
        using var test = new Fixture();
        string executable = test.MakeExecutableFixture();
        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))).ToLowerInvariant();

        var policy = new EuroOfficeNativeLabHostPolicy(test.Root, executable, sha);
        Assert.AreEqual(Path.GetFullPath(executable), policy.VerifyBinary());
        Assert.ThrowsExactly<InvalidDataException>(() =>
            new EuroOfficeNativeLabHostPolicy(test.Root, executable, new string('0', 64))
                .VerifyBinary());
        Assert.ThrowsExactly<ArgumentException>(() =>
            new EuroOfficeNativeLabHostPolicy(test.Root, executable, "bad-hash"));
    }

    [TestMethod]
    public void RejectsUnapprovedLocationAndRelativePaths()
    {
        using var test = new Fixture();
        string executable = test.MakeExecutableFixture();
        string outside = Path.Combine(Path.GetDirectoryName(test.Root)!, "outside-host.bin");
        Assert.ThrowsExactly<ArgumentException>(() =>
            new EuroOfficeNativeLabHostPolicy(test.Root, outside, new string('a', 64)).VerifyBinary());
        Assert.ThrowsExactly<ArgumentException>(() =>
            new EuroOfficeNativeLabHostPolicy(test.Root, "host.exe", new string('a', 64)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new EuroOfficeNativeLabHostPolicy("relative", executable, new string('a', 64)));
    }

    [TestMethod]
    public void RejectsMissingAndEmptyNativeProgram()
    {
        using var test = new Fixture();
        string exe = Path.Combine(test.Root, "host" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Assert.ThrowsExactly<FileNotFoundException>(() =>
            new EuroOfficeNativeLabHostPolicy(test.Root, exe, new string('a', 64)).VerifyBinary());
        File.WriteAllBytes(exe, []);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            new EuroOfficeNativeLabHostPolicy(test.Root, exe, new string('a', 64)).VerifyBinary());
    }

    [TestMethod]
    public void UnexpectedSdkCapabilitiesAreNotConsideredRuntimeProof()
    {
        var features = EuroOfficeDesktopProbeProtocol.ParseHello(
            $$"""
            {"protocol":"{{EuroOfficeDesktopProbeProtocol.Protocol}}",
             "engine":"EuroOfficeDesktopEditors",
             "upstreamCommit":"{{EuroOfficeDesktopProbeProtocol.PinnedUpstreamCommit}}",
             "features":["open-local"]}
            """);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            EuroOfficeDesktopProbeProtocol.Require(features, "open-local", labApproved: false));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "get-document-native-host-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public string MakeExecutableFixture()
        {
            string exe = Path.Combine(Root, "getdoc-eurooffice-qt-host" +
                (OperatingSystem.IsWindows() ? ".exe" : ""));
            File.WriteAllBytes(exe, [0x47, 0x45, 0x54, 0x21]);
            return exe;
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
