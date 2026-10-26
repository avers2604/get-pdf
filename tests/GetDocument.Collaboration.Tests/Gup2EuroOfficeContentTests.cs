using System.Net;
using System.Security.Cryptography;
using System.Text;
using GetDocument.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

[TestClass]
public sealed class Gup2EuroOfficeContentTests
{
    private const string Grant = "0123456789abcdef0123456789abcdef";
    private const string Employee = "11111111-2222-4333-8444-555555555555";
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes(
        "gd20-internal-euro-office-backend-test-secret-32");
    private static readonly Uri Gup2 = new("https://gup2.get.intra/documents/");
    private static readonly DateTimeOffset Now =
        DateTimeOffset.FromUnixTimeSeconds(1793034000);

    [TestMethod]
    public void ContentRequestUsesDistinctHmacScopeAndNeverPutsGrantInUrl()
    {
        using var request = Gup2EuroOfficeContentClient.CreateSignedRequest(
            Gup2, Grant, Employee, Secret, Now);
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("https://gup2.get.intra/documents/getdocument/eurooffice/content/",
            request.RequestUri!.AbsoluteUri);
        Assert.IsFalse(request.RequestUri.AbsoluteUri.Contains(Grant, StringComparison.Ordinal));
        Assert.AreEqual(0L, request.Content!.Headers.ContentLength);

        var bytes = Encoding.ASCII.GetBytes(
            "POST\ngetdocument/eurooffice/content\n" +
            Grant + "\n" + Employee + "\n" + Now.ToUnixTimeSeconds());
        var expected = Convert.ToHexString(HMACSHA256.HashData(Secret, bytes))
            .ToLowerInvariant();
        Assert.AreEqual(expected,
            request.Headers.GetValues("X-Get-Eurooffice-Signature").Single());
        using var metadata = Gup2EuroOfficeAttestationClient.CreateSignedRequest(
            Gup2, Grant, Employee, Secret, Now);
        Assert.AreNotEqual(
            metadata.Headers.GetValues("X-Get-Eurooffice-Signature").Single(),
            expected);
    }

    [TestMethod]
    public void VerifiesExactBytesBeforeReleasingThemToCaller()
    {
        byte[] content = Encoding.UTF8.GetBytes("GUP-2 immutable Office fixture");
        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var result = Gup2EuroOfficeContentClient.VerifyDownloadedContent(
            content, sha, maxBytes: 1024);
        CollectionAssert.AreEqual(content, result);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Gup2EuroOfficeContentClient.VerifyDownloadedContent(
                Encoding.UTF8.GetBytes("tampered"), sha, 1024));
    }

    [TestMethod]
    public void RejectsEmptyOversizedOrIncorrectDigest()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Gup2EuroOfficeContentClient.VerifyDownloadedContent(
                [], new string('a', 64), 1024));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Gup2EuroOfficeContentClient.VerifyDownloadedContent(
                new byte[12], new string('a', 64), 10));
        Assert.ThrowsExactly<ArgumentException>(() =>
            Gup2EuroOfficeContentClient.VerifyDownloadedContent(
                new byte[12], "not-a-sha", 1024));
    }

    [TestMethod]
    public void NeverTreatsRedirectConflictOrUnverifiedContentAsSuccess()
    {
        Gup2EuroOfficeContentClient.EnsureContentStatus(HttpStatusCode.OK);
        foreach (var status in new [] {
            HttpStatusCode.Redirect, HttpStatusCode.Found,
            HttpStatusCode.Forbidden, HttpStatusCode.Conflict,
            HttpStatusCode.Created, HttpStatusCode.NotFound })
        {
            Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
                Gup2EuroOfficeContentClient.EnsureContentStatus(status));
        }
    }

    [TestMethod]
    public void NoNetworkRequestWithMalformedIdentityOrExternalOrigin()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            Gup2EuroOfficeContentClient.CreateSignedRequest(
                new Uri("http://gup2.get.intra/documents/"), Grant,
                Employee, Secret, Now));
        Assert.ThrowsExactly<ArgumentException>(() =>
            Gup2EuroOfficeContentClient.CreateSignedRequest(
                Gup2, "not-a-grant", Employee, Secret, Now));
    }
}
