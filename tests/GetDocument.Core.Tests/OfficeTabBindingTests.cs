using GetDocument.Core.Integration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class OfficeTabBindingTests
{
    private const string Trusted = """
        {"Version":1,"AllowGup2TabBinding":true,
         "Gup2DocumentsUrl":"https://gup2.internal/documents/"}
        """;

    [TestMethod]
    public void TabBindingMustBeExplicitlyEnabledByIt()
    {
        Assert.IsFalse(OfficeTabBindingPolicy.Parse("""{"Version":1}""").Enabled);
        var approved = OfficeTabBindingPolicy.Parse(Trusted);
        Assert.IsTrue(approved.Enabled);
        Assert.AreEqual("https://gup2.internal/documents/getdocument/tab-binding/redeem/",
            approved.Destination().AbsoluteUri);
    }

    [DataTestMethod]
    [DataRow("http://gup2.internal/documents/")]
    [DataRow("https://user:pass@gup2.internal/documents/")]
    [DataRow("https://gup2.internal/documents/?code=abc")]
    [DataRow("https://gup2.internal/documents/#x")]
    [DataRow("https://gup2.internal/other/")]
    public void DangerousDestinationsCannotBeEnabled(string url)
    {
        string json = "{\"Version\":1,\"AllowGup2TabBinding\":true," +
            "\"Gup2DocumentsUrl\":\"" + url + "\"}";
        Assert.ThrowsExactly<InvalidDataException>(
            () => OfficeTabBindingPolicy.Parse(json));
    }

    [TestMethod]
    public void WrongOrMaliciousBindReceiptFailsClosed()
    {
        const string docId = "11111111-2222-3333-4444-555555555555";
        string sha = new string('a', 64);
        string valid = "{\"status\":\"bound\",\"document_id\":\"" + docId +
            "\",\"edit_version\":3,\"sha256\":\"" + sha +
            "\",\"extension\":\".docx\",\"reg_number\":\"Р-12\"}";
        var receipt = OfficeTabBindingClient.ParseReceipt(valid, sha, ".docx");
        Assert.AreEqual(Guid.Parse(docId), receipt.DocumentId);
        Assert.AreEqual(3, receipt.EditVersion);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            OfficeTabBindingClient.ParseReceipt(valid, new string('b', 64), ".docx"));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            OfficeTabBindingClient.ParseReceipt(valid, sha, ".xlsx"));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            OfficeTabBindingClient.ParseReceipt(valid.Replace(docId, "not-a-uuid"), sha, ".docx"));
    }

    [TestMethod]
    public async Task LinkedTabIsInvalidatedWhenLocalFileChanges()
    {
        string path = Path.Combine(Path.GetTempPath(), "getdoc-bind-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            string digest = await OfficeTabBindingClient.HashFileAsync(path, 1024);
            var receipt = new OfficeTabBindingReceipt(
                Guid.NewGuid(), 12, digest, ".docx", "GD14");
            var registry = new OfficeBoundTabRegistry();
            Guid tabId = Guid.NewGuid();
            registry.Set(tabId, path, receipt);
            Assert.IsNotNull(await registry.RevalidateAsync(tabId));
            await File.WriteAllBytesAsync(path, [3, 2, 1]);
            Assert.IsNull(await registry.RevalidateAsync(tabId));
            Assert.IsNull(registry.Get(tabId));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void CodeSyntaxAndFormatsAreNarrow()
    {
        Assert.IsTrue(OfficeTabBindingClient.IsValidCode(new string('A', 43)));
        Assert.IsFalse(OfficeTabBindingClient.IsValidCode(new string('A', 42)));
        Assert.IsFalse(OfficeTabBindingClient.IsValidCode("https://host.invalid/secret"));
        Assert.IsTrue(OfficeTabBindingClient.IsOfficeFile("x.docx"));
        Assert.IsTrue(OfficeTabBindingClient.IsOfficeFile("x.XLSX"));
        Assert.IsFalse(OfficeTabBindingClient.IsOfficeFile("x.pptx"));
    }
}
