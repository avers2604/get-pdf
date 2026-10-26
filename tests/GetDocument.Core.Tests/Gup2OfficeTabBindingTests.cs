using System.Net;
using GetDocument.Core.Integration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class Gup2OfficeTabBindingTests
{
    private const string BindingOnly = """
        {"Version":1,"AllowGup2TabBinding":true,
         "Gup2DocumentsUrl":"https://gup2.internal/documents/"}
        """;
    private const string Sha = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const string DocumentId = "11111111-2222-4333-8444-555555555555";

    [TestMethod]
    public void BindingPolicyIsIndependentFromOfficeUpload()
    {
        var policy = OfficeReturnPolicy.Parse(BindingOnly);
        Assert.IsFalse(policy.Enabled);
        Assert.IsTrue(policy.AllowGup2TabBinding);
        Assert.AreEqual("https://gup2.internal/documents/getdocument/tab-binding/redeem/",
            policy.BindingDestination().AbsoluteUri);
        Assert.ThrowsExactly<InvalidOperationException>(() => policy.Destination());
    }

    [TestMethod]
    public void BindingDisabledUnlessAdministratorExplicitlyEnablesIt()
    {
        var uploadOnly = OfficeReturnPolicy.Parse("""
            {"Version":1,"AllowGup2OfficeReturn":true,
             "Gup2DocumentsUrl":"https://gup2.internal/documents/"}
            """);
        Assert.ThrowsExactly<InvalidOperationException>(() => uploadOnly.BindingDestination());
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            OfficeReturnPolicy.Disabled.BindingDestination());
    }

    [DataTestMethod]
    [DataRow("http://gup2.invalid/documents/")]
    [DataRow("https://evil.invalid/documents/?token=secret")]
    [DataRow("https://gup2.invalid/other/")]
    [DataRow("https://name:secret@gup2.invalid/documents/")]
    public void BindingDoesNotWeakenTrustedHttpsOrigin(string destination)
    {
        var payload = "{\"Version\":1,\"AllowGup2TabBinding\":true,\"Gup2DocumentsUrl\":\"" +
            destination + "\"}";
        Assert.ThrowsExactly<InvalidDataException>(() => OfficeReturnPolicy.Parse(payload));
    }

    [TestMethod]
    public void AcceptsOnlyExactGup2ShaBoundMetadata()
    {
        string response = $$"""
            {"status":"bound","document_id":"{{DocumentId}}","edit_version":4,
             "sha256":"{{Sha}}","extension":".docx","reg_number":"ГЭТ-42"}
            """;
        var binding = Gup2OfficeTabBindingClient.ParseReceipt(response, Sha, ".docx");
        Assert.AreEqual(Guid.Parse(DocumentId), binding.DocumentId);
        Assert.AreEqual(4, binding.EditVersion);
        Assert.AreEqual(Sha, binding.Sha256);
        Assert.AreEqual(".docx", binding.Extension);
        Assert.AreEqual("ГЭТ-42", binding.RegistrationNumber);
    }

    [TestMethod]
    public void RefusesStaleShaExtensionOrUnsupportedPowerPoint()
    {
        string response = $$"""
            {"status":"bound","document_id":"{{DocumentId}}","edit_version":0,
             "sha256":"{{Sha}}","extension":".docx","reg_number":"42"}
            """;
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Gup2OfficeTabBindingClient.ParseReceipt(response, new string('a',64), ".docx"));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Gup2OfficeTabBindingClient.ParseReceipt(response, Sha, ".xlsx"));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Gup2OfficeTabBindingClient.ParseReceipt(response, Sha, ".pptx"));
    }

    [DataTestMethod]
    [DataRow("{\"status\":\"bound\",\"document_id\":\"bad\",\"edit_version\":0,\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"extension\":\".docx\"}")]
    [DataRow("{\"status\":\"working_copy_saved\"}")]
    [DataRow("{\"status\":\"bound\",\"storage_key\":\"/private/secret.docx\"}")]
    public void RefusesMissingOrUntrustedResponseFields(string response)
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Gup2OfficeTabBindingClient.ParseReceipt(response, Sha, ".docx"));
    }

    [TestMethod]
    public void NoRedirectsOrConflictsCanBeMistakenForVerifiedBinding()
    {
        Gup2OfficeTabBindingClient.EnsureBoundStatus(HttpStatusCode.OK);
        foreach (var code in new [] { HttpStatusCode.Found, HttpStatusCode.Conflict,
                     HttpStatusCode.Created, HttpStatusCode.NotFound, HttpStatusCode.Forbidden })
        {
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                Gup2OfficeTabBindingClient.EnsureBoundStatus(code));
        }
    }

    [TestMethod]
    public void InvalidCodeAndDocumentTypeAreRejectedBeforeNetwork()
    {
        var policy = OfficeReturnPolicy.Parse(BindingOnly);
        Assert.ThrowsExactly<ArgumentException>(() =>
            Gup2OfficeTabBindingClient.RedeemAsync("draft.docx", "x", policy)
                .GetAwaiter().GetResult());
        Assert.ThrowsExactly<ArgumentException>(() =>
            Gup2OfficeTabBindingClient.RedeemAsync("draft.pptx", new string('A',43), policy)
                .GetAwaiter().GetResult());
    }
}
