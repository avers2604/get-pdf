using GetDocument.Core.Integration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class OfficeReturnClientTests
{
    private const string Trusted = """
        {"Version":1,"AllowGup2OfficeReturn":true,
         "Gup2DocumentsUrl":"https://gup2.internal/documents/"}
        """;

    [TestMethod]
    public void PolicyIsDisabledByDefault()
    {
        var policy = OfficeReturnPolicy.Parse("""{"Version":1}""");
        Assert.IsFalse(policy.Enabled);
        Assert.ThrowsExactly<InvalidOperationException>(() => policy.Destination());
        Assert.IsFalse(OfficeReturnPolicy.Disabled.Enabled);
    }

    [TestMethod]
    public void ApprovedOfficeEndpointIsFixedHttpsPath()
    {
        var policy = OfficeReturnPolicy.Parse(Trusted);
        Assert.IsTrue(policy.Enabled);
        Assert.AreEqual(150L * 1024 * 1024, policy.MaxUploadBytes);
        Assert.AreEqual("https://gup2.internal/documents/getdocument/office-return/",
            policy.Destination().AbsoluteUri);
    }

    [DataTestMethod]
    [DataRow("http://gup2.internal/documents/")]
    [DataRow("https://u:p@gup2.internal/documents/")]
    [DataRow("https://gup2.internal/documents/?access_token=x")]
    [DataRow("https://gup2.internal/documents/#token")]
    [DataRow("https://gup2.internal/other/")]
    [DataRow("file:///tmp/file.docx")]
    public void DisallowsUntrustedAndCredentialBearingEndpoints(string url)
    {
        string policy = "{\"Version\":1,\"AllowGup2OfficeReturn\":true,\"Gup2DocumentsUrl\":\"" +
                        url + "\"}";
        Assert.ThrowsExactly<InvalidDataException>(() => OfficeReturnPolicy.Parse(policy));
    }

    [TestMethod]
    public void StrictOneUseCodeAndWorkingCopyValidation()
    {
        Assert.IsTrue(OfficeReturnClient.IsValidCode(new string('A', 43)));
        Assert.IsFalse(OfficeReturnClient.IsValidCode(new string('A', 42)));
        Assert.IsFalse(OfficeReturnClient.IsValidCode(new string('A', 42) + "/"));
        Assert.IsTrue(OfficeReturnClient.IsSupportedCopy("revision.docx"));
        Assert.IsTrue(OfficeReturnClient.IsSupportedCopy("revision.XLSX"));
        Assert.IsFalse(OfficeReturnClient.IsSupportedCopy("revision.pptx"));
        Assert.IsFalse(OfficeReturnClient.IsSupportedCopy("revision.pdf"));
        Assert.IsFalse(OfficeReturnClient.IsSupportedCopy(null));
        Assert.IsTrue(OfficeReturnClient.IsAllowedSize(1, 150L * 1024 * 1024));
        Assert.IsFalse(OfficeReturnClient.IsAllowedSize(0, 150L * 1024 * 1024));
        Assert.IsFalse(OfficeReturnClient.IsAllowedSize(150L * 1024 * 1024 + 1, 150L * 1024 * 1024));
        Assert.IsFalse(OfficeReturnClient.IsAllowedSize(1, long.MaxValue));
    }

    [TestMethod]
    public void InvalidArgumentsFailBeforeAnyNetwork()
    {
        var policy = OfficeReturnPolicy.Parse(Trusted);
        Assert.ThrowsExactly<ArgumentException>(() => OfficeReturnClient.UploadAsync(
            "draft.docx", "bad", policy).GetAwaiter().GetResult());
        Assert.ThrowsExactly<ArgumentException>(() => OfficeReturnClient.UploadAsync(
            "payload.exe", new string('A', 43), policy).GetAwaiter().GetResult());
        Assert.ThrowsExactly<InvalidOperationException>(() => OfficeReturnClient.UploadAsync(
            "draft.docx", new string('A', 43), OfficeReturnPolicy.Disabled).GetAwaiter().GetResult());
    }
}
