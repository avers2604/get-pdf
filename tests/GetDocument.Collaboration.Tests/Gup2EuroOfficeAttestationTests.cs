using System.Net;
using System.Security.Cryptography;
using System.Text;
using GetDocument.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

[TestClass]
public sealed class Gup2EuroOfficeAttestationTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes(
        "gd20-internal-euro-office-backend-test-secret-32");
    private static readonly DateTimeOffset Now =
        new(2026, 10, 26, 17, 0, 0, TimeSpan.Zero);
    private const string Grant = "0123456789abcdef0123456789abcdef";
    private const string Employee = "11111111-2222-4333-8444-555555555555";
    private const string Document = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
    private const string Sha = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [TestMethod]
    public void SignsExactlyTheGup2InterserviceChallengeWithoutPuttingGrantInTheUrl()
    {
        using var request = Gup2EuroOfficeAttestationClient.CreateSignedRequest(
            new Uri("https://gup2.get.intra/documents/"), Grant, Employee, Secret, Now);
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("https://gup2.get.intra/documents/getdocument/eurooffice/consume/",
            request.RequestUri!.AbsoluteUri);
        Assert.IsFalse(request.RequestUri!.AbsoluteUri.Contains(Grant, StringComparison.Ordinal));
        Assert.AreEqual(0L, request.Content!.Headers.ContentLength);
        Assert.AreEqual(Grant, request.Headers.GetValues("X-Get-Eurooffice-Nonce").Single());
        Assert.AreEqual(Employee, request.Headers.GetValues("X-Get-Eurooffice-User").Single());
        Assert.AreEqual(Now.ToUnixTimeSeconds().ToString(),
            request.Headers.GetValues("X-Get-Eurooffice-Timestamp").Single());
        byte[] expected = HMACSHA256.HashData(Secret, Encoding.ASCII.GetBytes(
            "POST\ngetdocument/eurooffice/consume\n" + Grant + "\n" +
            Employee + "\n" + Now.ToUnixTimeSeconds()));
        Assert.AreEqual(Convert.ToHexString(expected).ToLowerInvariant(),
            request.Headers.GetValues("X-Get-Eurooffice-Signature").Single());
    }

    [DataTestMethod]
    [DataRow("http://gup2.get.intra/documents/")]
    [DataRow("https://u:p@gup2.get.intra/documents/")]
    [DataRow("https://gup2.get.intra/documents/?next=evil")]
    [DataRow("https://gup2.get.intra/other/")]
    public void RefusesUntrustedServiceDestinations(string uri)
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            Gup2EuroOfficeAttestationClient.CreateSignedRequest(
                new Uri(uri), Grant, Employee, Secret, Now));
    }

    [TestMethod]
    public void RejectsMissingSecretMalformedIdentityAndOpaqueHandle()
    {
        var endpoint = new Uri("https://gup2.get.intra/documents/");
        Assert.ThrowsExactly<ArgumentException>(() =>
            Gup2EuroOfficeAttestationClient.CreateSignedRequest(
                endpoint, Grant, Employee, new byte[16], Now));
        Assert.ThrowsExactly<ArgumentException>(() =>
            Gup2EuroOfficeAttestationClient.CreateSignedRequest(
                endpoint, "0bad", Employee, Secret, Now));
        Assert.ThrowsExactly<ArgumentException>(() =>
            Gup2EuroOfficeAttestationClient.CreateSignedRequest(
                endpoint, Grant, "someone else", Secret, Now));
    }

    [TestMethod]
    public void AttestedMetadataIsNotAnEditableSessionOrContentCapability()
    {
        var json = $$"""
            {"status":"attested","document_id":"{{Document}}",
             "revision_id":"4","user_id":"{{Employee}}",
             "sha256":"{{Sha}}","extension":".docx","can_edit":false}
            """;
        var receipt = Gup2EuroOfficeAttestationClient.ParseAttestation(json, Employee);
        Assert.AreEqual(Guid.Parse(Document), receipt.DocumentId);
        Assert.AreEqual(Employee, receipt.EmployeeId);
        Assert.AreEqual("4", receipt.RevisionId);
        Assert.AreEqual(Sha, receipt.Sha256);
        Assert.IsFalse(receipt.CanEdit);
    }

    [DataTestMethod]
    [DataRow("true")]
    [DataRow("false")]
    public void RejectsUnexpectedEditingFlags(string editing)
    {
        var json = $$"""
            {"status":"attested","document_id":"{{Document}}",
             "revision_id":"4","user_id":"{{Employee}}",
             "sha256":"{{Sha}}","extension":".docx","can_edit":{{editing}},
             "document_url":"https://gup2.get.intra/private/file.docx"}
            """;
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Gup2EuroOfficeAttestationClient.ParseAttestation(json, Employee));
    }

    [TestMethod]
    public void DoesNotAcceptAnAttestationForADifferentEmployee()
    {
        string json = $$"""
            {"status":"attested","document_id":"{{Document}}",
             "revision_id":"4","user_id":"{{Employee}}",
             "sha256":"{{Sha}}","extension":".docx","can_edit":false}
            """;
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Gup2EuroOfficeAttestationClient.ParseAttestation(json,
                "22222222-2222-4333-8444-555555555555"));
    }

    [TestMethod]
    public void OnlyExactOkResponseMeansAttestation()
    {
        Gup2EuroOfficeAttestationClient.EnsureSuccessfulStatus(HttpStatusCode.OK);
        foreach (var status in new [] {
            HttpStatusCode.Created, HttpStatusCode.Redirect, HttpStatusCode.Unauthorized,
            HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.Conflict
        })
            Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
                Gup2EuroOfficeAttestationClient.EnsureSuccessfulStatus(status));
    }
}
