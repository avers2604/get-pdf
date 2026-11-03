using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27ak pure, cross-platform unit contracts. These tests never start
/// Windows pipes, launch subprocesses or touch any user document.
/// </summary>
[TestClass]
public sealed class WindowsNamedPipeDenialLabTests
{
    private static readonly Guid RunId =
        Guid.ParseExact("632a91807f1149f0b77db8aa1fb00417", "N");

    [TestMethod]
    public void PipeNameIsFixedAndScopedToAnOperatorPinnedRun()
    {
        Assert.AreEqual(
            "getdoc-gd31j27ak-denial-632a91807f1149f0b77db8aa1fb00417",
            WindowsNamedPipeDenialLab.PipeName(RunId));
        Assert.ThrowsExactly<ArgumentException>(
            () => WindowsNamedPipeDenialLab.PipeName(Guid.Empty));
    }

    [TestMethod]
    public void ExactReadOnlySyntheticRequestIsTheOnlyPermittedInput()
    {
        byte[] request = WindowsNamedPipeDenialLab.Request;
        Assert.AreEqual(
            "GETDOC_GD31J27AK_WINDOWS_PIPE_DEVNULL_FREE_DENIAL_ONLY\n",
            System.Text.Encoding.ASCII.GetString(request));
        WindowsNamedPipeDenialLab.RequireExactRequest(request);
        foreach (byte[] bad in new[]
        {
            Array.Empty<byte>(),
            request[..^1],
            request.Concat(new byte[] { 0 }).ToArray(),
            System.Text.Encoding.ASCII.GetBytes("SAVE:\\source.docx\n")
        })
            Assert.ThrowsExactly<InvalidDataException>(
                () => WindowsNamedPipeDenialLab.RequireExactRequest(bad));
    }

    [TestMethod]
    public void ClientPidMustBeKernelObservedAndDifferentFromServer()
    {
        WindowsNamedPipeDenialLab.RequireMatchingClientPid(
            observedPid: 12002, pinnedPid: 12002, serverPid: 12001);
        foreach (var sample in new[]
        {
            (observed: 12003u, pinned: 12002, server: 12001),
            (observed: 12002u, pinned: 12002, server: 12002),
            (observed: 0u, pinned: 0, server: 12001),
            (observed: 3u, pinned: 3, server: 12001)
        })
            Assert.ThrowsExactly<InvalidDataException>(
                () => WindowsNamedPipeDenialLab.RequireMatchingClientPid(
                    sample.observed, sample.pinned, sample.server));
    }

    [TestMethod]
    public void DenialReplyNeverClaimsSaveOrReleaseRights()
    {
        string reply = System.Text.Encoding.ASCII.GetString(
            WindowsNamedPipeDenialLab.Denied);
        Assert.AreEqual(
            "{\"state\":\"denied\",\"laboratoryOnly\":true," +
            "\"nativePublicationAuthorized\":false,\"releaseReady\":false," +
            "\"writePermissionGranted\":false}\n",
            reply);
        WindowsNamedPipeDenialLab.RequireExactDenial(
            WindowsNamedPipeDenialLab.Denied);
        Assert.ThrowsExactly<InvalidDataException>(
            () => WindowsNamedPipeDenialLab.RequireExactDenial(
                System.Text.Encoding.ASCII.GetBytes("{\"state\":\"saved\"}\n")));
        Assert.IsFalse(WindowsNamedPipeDenialLab.NativePublicationAuthorized);
        Assert.IsFalse(WindowsNamedPipeDenialLab.ReleaseReady);
    }
}
