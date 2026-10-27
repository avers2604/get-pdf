using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using GetDocument.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

[TestClass]
public sealed class Gup2EuroOfficeEditorSessionTests
{
    private static readonly byte[] Secret =
        Encoding.UTF8.GetBytes("gd25-local-unique-server-session-hmac-key-for-tests-32bytes");
    private static readonly Uri TrustedOrigin =
        new("https://gup2.intra.get.test/documents/");
    private const string LaunchId = "0123456789abcdef0123456789abcdef";
    private const string Employee = "11111111-2222-4333-8444-555555555555";
    private const string Document = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
    private const int Revision = 4;
    private static readonly DateTimeOffset Clock =
        DateTimeOffset.FromUnixTimeSeconds(1793034000);

    private static string Json(
        string user = Employee, string document = Document, int edit = Revision)
    {
        var key = Gup2EuroOfficeEditorSessionClient.CalculateDocumentKey(document, edit);
        return $$"""
            {"status":"session_bound","document_id":"{{document}}",
             "user_id":"{{user}}","auth_version":3,"edit_version":{{edit}},
             "document_key":"{{key}}","original_sha256":"{{new string('1', 64)}}",
             "sha256":"{{new string('2', 64)}}","extension":".docx",
             "save_grant":"{{new string('a', 64)}}"}
            """;
    }

    [TestMethod]
    public void HmacMatchesGup2CanonicalLfAndContainsNoSecretInUrl()
    {
        using var request = Gup2EuroOfficeEditorSessionClient.CreateSignedRequest(
            TrustedOrigin, LaunchId, Employee, Secret, Clock);
        string signed = "POST\ngetdocument/eurooffice/session/redeem\n" +
            LaunchId + "\n" + Employee + "\n" + Clock.ToUnixTimeSeconds();
        string expected = Convert.ToHexString(HMACSHA256.HashData(
            Secret, Encoding.ASCII.GetBytes(signed))).ToLowerInvariant();
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("https://gup2.intra.get.test/documents/getdocument/eurooffice/session/redeem/",
            request.RequestUri!.AbsoluteUri);
        Assert.IsFalse(request.RequestUri.AbsoluteUri.Contains(LaunchId, StringComparison.Ordinal));
        Assert.AreEqual(expected,
            request.Headers.GetValues("X-Get-Eurooffice-Signature").Single());
        Assert.AreEqual(0L, request.Content!.Headers.ContentLength);
    }

    [TestMethod]
    public void ParsesOnlySessionBoundToSameGup2SsoIdentityAndRevision()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Employee)], "corporate"));
        var receipt = Gup2EuroOfficeEditorSessionClient.ParseReceipt(
            Json(), principal);
        Assert.AreEqual(Guid.Parse(Document), receipt.DocumentId);
        Assert.AreEqual(Employee, receipt.EmployeeId);
        Assert.AreEqual(Revision, receipt.EditVersion);
        Assert.AreEqual(3, receipt.ActorAuthVersion);
        Assert.AreEqual(new string('a', 64), receipt.SaveGrant);
        Assert.AreEqual(new string('2', 64), receipt.WorkingSha256);
        Assert.IsFalse(receipt.ToString()!.Contains(new string('a', 64)));
    }

    [TestMethod]
    public void RejectsAnonymousOrDifferentSsoEmployee()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            Gup2EuroOfficeEditorSessionClient.ParseReceipt(Json(), anonymous));
        var other = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier,
                "22222222-2222-4333-8444-555555555555")], "corporate"));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            Gup2EuroOfficeEditorSessionClient.ParseReceipt(Json(), other));
    }

    [TestMethod]
    public void RejectsUnexpectedFieldsStaleDocumentKeyAndNonCanonicalGrant()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Employee)], "corporate"));
        string valid = Json();
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Gup2EuroOfficeEditorSessionClient.ParseReceipt(
                valid.Replace("session_bound", "metadata_only"), principal));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Gup2EuroOfficeEditorSessionClient.ParseReceipt(
                valid.Replace("}", ",\"file_url\":\"https://evil.invalid/a.docx\"}"), principal));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Gup2EuroOfficeEditorSessionClient.ParseReceipt(
                valid.Replace(new string('a', 64), "not-a-grant"), principal));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Gup2EuroOfficeEditorSessionClient.ParseReceipt(
                valid.Replace("\"edit_version\":4", "\"edit_version\":5"), principal));
    }

    [TestMethod]
    public void RejectsUntrustedHttpsDestinationAndMalformedNonce()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            Gup2EuroOfficeEditorSessionClient.CreateSignedRequest(
                new Uri("http://gup2.intra.get.test/documents/"),
                LaunchId, Employee, Secret, Clock));
        Assert.ThrowsExactly<ArgumentException>(() =>
            Gup2EuroOfficeEditorSessionClient.CreateSignedRequest(
                new Uri("https://gup2.intra.get.test/else/"),
                LaunchId, Employee, Secret, Clock));
        Assert.ThrowsExactly<ArgumentException>(() =>
            Gup2EuroOfficeEditorSessionClient.CreateSignedRequest(
                TrustedOrigin, "bad", Employee, Secret, Clock));
        Assert.ThrowsExactly<ArgumentException>(() =>
            Gup2EuroOfficeEditorSessionClient.CreateSignedRequest(
                TrustedOrigin, LaunchId, Employee, new byte[8], Clock));
    }
}
