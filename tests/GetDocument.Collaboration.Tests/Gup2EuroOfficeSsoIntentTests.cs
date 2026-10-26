using System.Net;
using System.Security.Cryptography;
using System.Text;
using GetDocument.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

[TestClass]
public sealed class Gup2EuroOfficeSsoIntentTests
{
    private const string Code =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Actor = "11111111-2222-4333-8444-555555555555";
    private const string Document = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
    private const string FileSha =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const string Grant =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly Uri Gup2 = new("https://gup2.get.intra/documents/");
    private static readonly byte[] Secret = Encoding.ASCII.GetBytes(
        "gd25-demo-internal-intent-hmac-key-never-use-in-production");
    private static readonly DateTimeOffset Now =
        DateTimeOffset.FromUnixTimeSeconds(1793034000);

    private static string EditorKey => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Document + "\n7")))
        .ToLowerInvariant();

    private static string Sample(string user = Actor, string? key = null) =>
        $$"""
          {"status":"session_authorized","document_id":"{{Document}}",
           "user_id":"{{user}}","edit_version":7,
           "sha256":"{{FileSha}}","extension":".docx",
           "document_key":"{{key ?? EditorKey}}","save_grant":"{{Grant}}"}
          """;

    [TestMethod]
    public void HmacRedeemMatchesTheExactGup2BackendProtocol()
    {
        using var request = Gup2EuroOfficeSsoIntentClient.CreateSignedRequest(
            Gup2, Code, Actor, Secret, Now);
        string signed = "POST\ngetdocument/eurooffice/sso-intent/redeem\n" +
                        Code + "\n" + Actor + "\n" + Now.ToUnixTimeSeconds();
        string proof = Convert.ToHexString(
            HMACSHA256.HashData(Secret, Encoding.ASCII.GetBytes(signed)))
            .ToLowerInvariant();
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("https://gup2.get.intra/documents/getdocument/eurooffice/sso-intent/redeem/",
            request.RequestUri!.AbsoluteUri);
        Assert.AreEqual(0L, request.Content!.Headers.ContentLength);
        Assert.AreEqual(Code,
            request.Headers.GetValues("X-Get-Eurooffice-Intent-Code").Single());
        Assert.AreEqual(proof,
            request.Headers.GetValues("X-Get-Eurooffice-Signature").Single());
    }

    [TestMethod]
    public void OnlyMatchingCorporatePrincipalGetsPrivateSaveGrant()
    {
        var data = Gup2EuroOfficeSsoIntentClient.ParseRedemption(
            HttpStatusCode.OK, Sample(), Actor);
        Assert.AreEqual(Guid.Parse(Document), data.DocumentId);
        Assert.AreEqual(Actor, data.UserId);
        Assert.AreEqual(7, data.EditVersion);
        Assert.AreEqual(EditorKey, data.DocumentKey);
        Assert.AreEqual(Grant, data.ServerSaveGrant);
        Assert.AreEqual(FileSha, data.Sha256);
    }

    [TestMethod]
    public void Gup2InitialWorkingRevisionZeroHasAValidEditorKey()
    {
        string initialKey = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Document + "\n0"))).ToLowerInvariant();
        string zeroRevision = Sample().Replace(
            "\"edit_version\":7", "\"edit_version\":0")
            .Replace(EditorKey, initialKey);
        var response = Gup2EuroOfficeSsoIntentClient.ParseRedemption(
            HttpStatusCode.OK, zeroRevision, Actor);
        Assert.AreEqual(0, response.EditVersion);
        Assert.AreEqual(initialKey, response.DocumentKey);
    }

    [TestMethod]
    public void RejectsForgedActorAndRewrittenOfficeDocumentKey()
    {
        var other = "22222222-2222-4333-8444-555555555555";
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Gup2EuroOfficeSsoIntentClient.ParseRedemption(
                HttpStatusCode.OK, Sample(user: other), Actor));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Gup2EuroOfficeSsoIntentClient.ParseRedemption(
                HttpStatusCode.OK, Sample(key: new string('f', 64)), Actor));
    }

    [TestMethod]
    public void ExtraMetadataSecretsAndHttpRedirectCannotAuthorize()
    {
        string response = Sample();
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Gup2EuroOfficeSsoIntentClient.ParseRedemption(
                HttpStatusCode.OK, response.Replace("}", ",\"callback_url\":\"http://evil/\"}"),
                Actor));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            Gup2EuroOfficeSsoIntentClient.ParseRedemption(
                HttpStatusCode.Found, response, Actor));
    }

    [TestMethod]
    public void NoUntrustedHostOrMalformedCodeIsAllowed()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            Gup2EuroOfficeSsoIntentClient.CreateSignedRequest(
                new Uri("http://gup2.get.intra/documents/"), Code, Actor, Secret, Now));
        Assert.ThrowsExactly<ArgumentException>(() =>
            Gup2EuroOfficeSsoIntentClient.CreateSignedRequest(
                Gup2, "short", Actor, Secret, Now));
        Assert.ThrowsExactly<ArgumentException>(() =>
            Gup2EuroOfficeSsoIntentClient.CreateSignedRequest(
                Gup2, Code, Actor, new byte[8], Now));
    }
}
