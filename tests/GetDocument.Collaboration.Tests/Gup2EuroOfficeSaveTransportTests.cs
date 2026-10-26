using System.Net;
using System.Security.Cryptography;
using System.Text;
using GetDocument.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

[TestClass]
public sealed class Gup2EuroOfficeSaveTransportTests
{
    private const string Grant = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Employee = "11111111-2222-4333-8444-555555555555";
    private static readonly string DocumentKey = Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee" + "\n" + "4")))
        .ToLowerInvariant();
    private static readonly byte[] Secret = Encoding.ASCII.GetBytes(
        "gd24-local-only-internal-save-transport-key-64-characters");
    private static readonly byte[] Content = Encoding.ASCII.GetBytes("GD24-NEW-OFFICE-COPY");
    private static readonly string Fingerprint =
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes("GD24-signed-callback")))
            .ToLowerInvariant();
    private static readonly Uri Gup2 = new("https://gup2.get.intra/documents/");
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1793034000);

    [TestMethod]
    public void HmacCoversTheExactBodyDigestEmployeeAndCallbackFingerprint()
    {
        using var req = Gup2EuroOfficeSaveTransportClient.CreateSignedRequest(
            Gup2, Grant, Employee, DocumentKey, Secret, Now, Content, Fingerprint);
        string sha = Convert.ToHexString(SHA256.HashData(Content)).ToLowerInvariant();
        string message = "POST\ngetdocument/eurooffice/save\n" + Grant + "\n" + Employee +
            "\n" + Now.ToUnixTimeSeconds() + "\n" + DocumentKey + "\n" + sha +
            "\n" + Fingerprint +
            "\n" + Content.Length;
        string signature = Convert.ToHexString(HMACSHA256.HashData(
            Secret, Encoding.ASCII.GetBytes(message))).ToLowerInvariant();
        Assert.AreEqual("https://gup2.get.intra/documents/getdocument/eurooffice/save/",
            req.RequestUri!.AbsoluteUri);
        Assert.IsFalse(req.RequestUri.AbsoluteUri.Contains(Grant, StringComparison.Ordinal));
        Assert.AreEqual((long)Content.Length, req.Content!.Headers.ContentLength);
        Assert.AreEqual(DocumentKey,
            req.Headers.GetValues("X-Get-Eurooffice-Document-Key").Single());
        Assert.AreEqual(sha, req.Headers.GetValues("X-Get-Eurooffice-File-Sha256").Single());
        Assert.AreEqual(Fingerprint,
            req.Headers.GetValues("X-Get-Eurooffice-Callback-Fingerprint").Single());
        Assert.AreEqual(signature,
            req.Headers.GetValues("X-Get-Eurooffice-Signature").Single());
    }

    [TestMethod]
    public void OnlyRealPersistedGup2RevisionIsSuccessful()
    {
        var sha = Convert.ToHexString(SHA256.HashData(Content)).ToLowerInvariant();
        var saved = $$"""{"status":"working_copy_saved","edit_version":7,"sha256":"{{sha}}"}""";
        var result = Gup2EuroOfficeSaveTransportClient.ParseReceipt(
            HttpStatusCode.Created, saved, sha);
        Assert.IsTrue(result.Committed);
        Assert.AreEqual(7, result.EditVersion);
        Assert.AreEqual(sha, result.Sha256);
        var repeated = Gup2EuroOfficeSaveTransportClient.ParseReceipt(
            HttpStatusCode.OK, saved.Replace("working_copy_saved", "already_committed"), sha);
        Assert.IsTrue(repeated.Committed);
    }

    [TestMethod]
    public void DoesNotAcknowledgeMismatchRedirectOrAdditionalSecrets()
    {
        var sha = Convert.ToHexString(SHA256.HashData(Content)).ToLowerInvariant();
        var saved = $$"""{"status":"working_copy_saved","edit_version":7,"sha256":"{{sha}}"}""";
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Gup2EuroOfficeSaveTransportClient.ParseReceipt(
                HttpStatusCode.Created, saved, new string('a', 64)));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            Gup2EuroOfficeSaveTransportClient.ParseReceipt(HttpStatusCode.Redirect, saved, sha));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Gup2EuroOfficeSaveTransportClient.ParseReceipt(
                HttpStatusCode.Created,
                saved.Replace("}", ",\"storage_key\":\"/private/secret\"}"), sha));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Gup2EuroOfficeSaveTransportClient.ParseReceipt(HttpStatusCode.OK, saved, sha));
    }

    [TestMethod]
    public void FailsClosedForUntrustedOriginsGrantsAndKeys()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            Gup2EuroOfficeSaveTransportClient.CreateSignedRequest(
                new Uri("http://gup2.get.intra/documents/"), Grant,
                Employee, Secret, Now, Content, Fingerprint));
        Assert.ThrowsExactly<ArgumentException>(() =>
            Gup2EuroOfficeSaveTransportClient.CreateSignedRequest(
                Gup2, Grant, Employee, DocumentKey, new byte[16], Now, Content, Fingerprint));
        Assert.ThrowsExactly<ArgumentException>(() =>
            Gup2EuroOfficeSaveTransportClient.CreateSignedRequest(
                Gup2, "bad", Employee, DocumentKey, Secret, Now, Content, Fingerprint));
        Assert.ThrowsExactly<ArgumentException>(() =>
            Gup2EuroOfficeSaveTransportClient.CreateSignedRequest(
                Gup2, Grant, Employee, DocumentKey, Secret, Now, [], Fingerprint));
    }
}
