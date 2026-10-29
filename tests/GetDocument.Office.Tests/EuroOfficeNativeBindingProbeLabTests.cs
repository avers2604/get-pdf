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
    public void RefusesProductionLaunchWithoutSeparateLabApproval()
    {
        // Rejection happens BEFORE reading or launching arbitrary native binary.
        Assert.ThrowsExactly<NotSupportedException>(() =>
            EuroOfficeNativeBindingProbeLab.CreateStartInfo(
                null!, "/fixture-lab", "gd31j6_writer.docx",
                Guid.NewGuid(), Guid.NewGuid(), labApproved: false));
    }
}
