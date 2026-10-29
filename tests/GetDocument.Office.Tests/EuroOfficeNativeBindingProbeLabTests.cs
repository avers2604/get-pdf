using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeBindingProbeLabTests
{
    [TestMethod]
    public async Task ReadsExactlyOneBoundedNativeReceipt()
    {
        string json = "{\"schema\":\"getdoc-native-binding-lab/1\"}";
        using var reader = new StringReader(json + "\n");
        Assert.AreEqual(json,
            await EuroOfficeNativeBindingProbeLab.ReadOnlyReceiptAsync(
                reader, CancellationToken.None));
    }

    [TestMethod]
    public async Task RejectsTruncatedDuplicateOrUnboundedNativeOutput()
    {
        foreach (string text in new[]
        {
            "",
            "{}",
            "\n",
            "{}\n{}\n",
            "{}\nSTDOUT-LOG\n",
            "{}\r\n",
            "{}\0\n",
            new string('X', 2049) + "\n"
        })
        {
            using var reader = new StringReader(text);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                EuroOfficeNativeBindingProbeLab.ReadOnlyReceiptAsync(
                    reader, CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task ExplicitSaveAsV2TransportAcceptsBoundedReceiptLargerThanLegacy()
    {
        // A valid v2 JSON may be longer than the old generic 2048-char
        // binding transport guard, but it must still be <= the v2 parser's
        // published 4096-char limit. Whitespace is merely parser-valid
        // padding; no Qt process or user document is involved.
        string v2 = "{\"schema\":\"getdoc-native-saveas-event-lab/2\"}" +
            new string(' ', 2800);
        using var reader = new StringReader(v2 + "\n");
        Assert.AreEqual(v2,
            await EuroOfficeNativeBindingProbeLab.ReadOnlyReceiptAsync(
                reader, CancellationToken.None, maxChars: 4096));
    }

    [TestMethod]
    public async Task SaveAsV2StillRejectsOverLimitOutputExtraLinesAndMissingNewline()
    {
        foreach (string text in new[]
        {
            new string('X', 4097) + "\n",
            "{}\n{}\n",
            "{}\nQt stderr accidentally printed on stdout\n",
            "{}" + new string(' ', 3500), // truncated receipt
            "{}\r\n",
            "{}\0\n"
        })
        {
            using var reader = new StringReader(text);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                EuroOfficeNativeBindingProbeLab.ReadOnlyReceiptAsync(
                    reader, CancellationToken.None, maxChars: 4096));
        }
    }

    [TestMethod]
    public async Task LegacyBindingReceiptKeepsOriginal2048CharacterLimit()
    {
        string legacy = "{\"schema\":\"getdoc-native-binding-lab/1\"}" +
            new string(' ', 2400);
        using var reader = new StringReader(legacy + "\n");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            EuroOfficeNativeBindingProbeLab.ReadOnlyReceiptAsync(
                reader, CancellationToken.None));
    }

    [TestMethod]
    public async Task ArbitraryTransportLimitCannotBeUsedToDisableSizeGate()
    {
        using var reader = new StringReader("{}\n");
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            EuroOfficeNativeBindingProbeLab.ReadOnlyReceiptAsync(
                reader, CancellationToken.None, maxChars: 65536));
    }

    [TestMethod]
    public void RefusesProductionLaunchWithoutSeparateLabApproval()
    {
        // Rejection happens BEFORE reading or launching arbitrary native binary.
        Assert.ThrowsExactly<NotSupportedException>(() =>
            EuroOfficeNativeBindingProbeLab.CreateStartInfo(
                null!, "/fixture-lab", "gd31j6_writer.docx",
                Guid.NewGuid(), Guid.NewGuid(), labApproved: false));
    }
}
