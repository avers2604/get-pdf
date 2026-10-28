using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeDesktopProbeTests
{
    private static string Reply(params string[] features) =>
        JsonSerializer.Serialize(new
        {
            protocol = EuroOfficeDesktopProbeProtocol.Protocol,
            engine = "EuroOfficeDesktopEditors",
            upstreamCommit = EuroOfficeDesktopProbeProtocol.PinnedUpstreamCommit,
            features
        });

    [TestMethod]
    public void HandshakeDefaultsToNoEditorPrivileges()
    {
        var capabilities = EuroOfficeDesktopProbeProtocol.ParseHello(Reply());
        Assert.AreEqual(0, capabilities.Features.Count);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            EuroOfficeDesktopProbeProtocol.Require(capabilities, "open-local", labApproved: false));
    }

    [TestMethod]
    public void EvenPositiveHandshakeRequiresExplicitLabApproval()
    {
        var capabilities = EuroOfficeDesktopProbeProtocol.ParseHello(
            Reply("open-local", "save-as-new-revision", "export-pdf", "resize"));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            EuroOfficeDesktopProbeProtocol.Require(capabilities, "open-local", labApproved: false));
        EuroOfficeDesktopProbeProtocol.Require(capabilities, "open-local", labApproved: true);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            EuroOfficeDesktopProbeProtocol.Require(capabilities, "print-direct", labApproved: true));
    }

    [TestMethod]
    public void RejectsUnknownDuplicateOrForgedCapabilities()
    {
        foreach (string[] features in new[]
        {
            new[] { "run-macro" },
            new[] { "open-local", "open-local" },
            new[] { "open-local", "" },
            new[] { "print-direct" },
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                EuroOfficeDesktopProbeProtocol.ParseHello(Reply(features)));
    }

    [TestMethod]
    public void RejectsWrongEngineCommitProtocolAndAdditionalKeys()
    {
        string valid = Reply("open-local");
        foreach (string bad in new[]
        {
            valid.Replace("EuroOfficeDesktopEditors", "LibreOffice"),
            valid.Replace(EuroOfficeDesktopProbeProtocol.PinnedUpstreamCommit, new string('f', 40)),
            valid.Replace(EuroOfficeDesktopProbeProtocol.Protocol, "getdoc-eurodesktop-probe/2"),
            valid.Replace("\"engine\":", "\"unknown\":1,\"engine\":"),
            "{}",
            "{",
            new string('A', 16 * 1024),
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                EuroOfficeDesktopProbeProtocol.ParseHello(bad));
    }

    [TestMethod]
    public void NoActiveCommandsForWormDocuments()
    {
        var capabilities = EuroOfficeDesktopProbeProtocol.ParseHello(
            Reply("open-local", "save-as-new-revision", "export-pdf", "resize"));
        EuroOfficeDesktopProbeProtocol.Require(capabilities, "open-local", labApproved: true,
            wormOriginal: true);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            EuroOfficeDesktopProbeProtocol.Require(capabilities, "save-as-new-revision",
                labApproved: true, wormOriginal: true));
    }

    [TestMethod]
    public void FilesMustBeLocalStagedAndNeverOverwriteExistingRevisions()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gd31j-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "Акт.docx");
            File.WriteAllText(source, "test-fixture-only");
            string destination = Path.Combine(directory, "Акт-редакция.docx");
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                EuroOfficeDesktopProbeProtocol.CreateSaveRequest(directory, source, destination));
            var accepted = EuroOfficeDesktopProbeProtocol.CreateSaveRequest(directory, source, destination, wormOriginal: false);
            using var message = JsonDocument.Parse(accepted);
            Assert.AreEqual("save-as-new-revision", message.RootElement.GetProperty("command").GetString());
            Assert.AreEqual(Path.GetFullPath(destination),
                message.RootElement.GetProperty("destination").GetString());
            File.WriteAllText(destination, "do not overwrite");
            Assert.ThrowsExactly<IOException>(() =>
                EuroOfficeDesktopProbeProtocol.CreateSaveRequest(directory, source, destination, wormOriginal: false));
            Assert.AreEqual("do not overwrite", File.ReadAllText(destination));

            Assert.ThrowsExactly<ArgumentException>(() =>
                EuroOfficeDesktopProbeProtocol.CreateSaveRequest(
                    directory, source, Path.Combine(directory, "..", "escape.docx"), wormOriginal: false));
            Assert.ThrowsExactly<ArgumentException>(() =>
                EuroOfficeDesktopProbeProtocol.CreateSaveRequest(directory, source,
                    Path.Combine(directory, "Акт-редакция.xlsm"), wormOriginal: false));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task LineProtocolHandshakeOnlyUsesExplicitHelloAndDoesNotOpenFiles()
    {
        using var input = new StringReader(Reply("open-local") + "\n");
        using var output = new StringWriter();
        var info = await EuroOfficeDesktopProbeProtocol.ExchangeHelloAsync(
            input, output, CancellationToken.None);
        CollectionAssert.Contains(info.Features.ToArray(), "open-local");
        using var query = JsonDocument.Parse(output.ToString().Trim());
        Assert.AreEqual("hello", query.RootElement.GetProperty("command").GetString());
        Assert.AreEqual(EuroOfficeDesktopProbeProtocol.Protocol,
            query.RootElement.GetProperty("protocol").GetString());
        Assert.IsFalse(output.ToString().Contains("source", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task HandshakeRejectsEofAndRespectsCancellation()
    {
        using var input = new StringReader("");
        using var output = new StringWriter();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            EuroOfficeDesktopProbeProtocol.ExchangeHelloAsync(input, output, CancellationToken.None));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            EuroOfficeDesktopProbeProtocol.ExchangeHelloAsync(
                new StringReader(Reply()), new StringWriter(), cts.Token));
    }

    [TestMethod]
    public void ResizeRejectsInvalidOrUnboundedGeometry()
    {
        using var valid = JsonDocument.Parse(EuroOfficeDesktopProbeProtocol.CreateResizeRequest(
            1280, 720, 1.5));
        Assert.AreEqual("resize", valid.RootElement.GetProperty("command").GetString());
        foreach (double scale in new[] { double.NaN, double.PositiveInfinity, 0.1, 9.0 })
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                EuroOfficeDesktopProbeProtocol.CreateResizeRequest(1920, 1080, scale));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            EuroOfficeDesktopProbeProtocol.CreateResizeRequest(0, 1080, 1.0));
    }

    [TestMethod]
    public void PdfExportIsOnlyAStagedRequestForTheGetPrintWizard()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gd31j-print-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "План.pptx");
            string pdf = Path.Combine(directory, "План-печать.pdf");
            File.WriteAllBytes(source, [0x50, 0x4B, 0x03, 0x04]);
            string request = EuroOfficeDesktopProbeProtocol.CreateExportPdfRequest(directory, source, pdf);
            using var message = JsonDocument.Parse(request);
            Assert.AreEqual("export-pdf", message.RootElement.GetProperty("command").GetString());
            Assert.AreEqual(Path.GetFullPath(pdf),
                message.RootElement.GetProperty("destination").GetString());
            Assert.IsFalse(File.Exists(pdf), "A probe request may never claim export completed.");

            File.WriteAllText(pdf, "existing WORM/print copy");
            Assert.ThrowsExactly<IOException>(() =>
                EuroOfficeDesktopProbeProtocol.CreateExportPdfRequest(directory, source, pdf));
            Assert.AreEqual("existing WORM/print copy", File.ReadAllText(pdf));

            Assert.ThrowsExactly<ArgumentException>(() =>
                EuroOfficeDesktopProbeProtocol.CreateExportPdfRequest(
                    directory, source, Path.Combine(directory, "..", "escaped.pdf")));
            Assert.ThrowsExactly<ArgumentException>(() =>
                EuroOfficeDesktopProbeProtocol.CreateExportPdfRequest(
                    directory, source, Path.Combine(directory, "wrong.docx")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void OpeningRequiresSafeStagingAndNeverAcceptsUrlOrMacros()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gd31j-open-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "Ведомость.xlsx");
            File.WriteAllText(source, "test-fixture-only");
            string request = EuroOfficeDesktopProbeProtocol.CreateOpenRequest(directory, source);
            using var msg = JsonDocument.Parse(request);
            Assert.AreEqual("open-local", msg.RootElement.GetProperty("command").GetString());
            Assert.ThrowsExactly<ArgumentException>(() =>
                EuroOfficeDesktopProbeProtocol.CreateOpenRequest(directory, "https://host/document.xlsx"));
            string macro = Path.Combine(directory, "macro.xlsm");
            File.WriteAllText(macro, "test-fixture-only");
            Assert.ThrowsExactly<NotSupportedException>(() =>
                EuroOfficeDesktopProbeProtocol.CreateOpenRequest(directory, macro));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
