using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using GetDocument.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

[TestClass]
public sealed class PinnedEuroOfficeDownloaderTests
{
    private static readonly Uri Office = new("https://office.get.intra/");
    private static readonly IPAddress ApprovedIp = IPAddress.Parse("10.54.22.17");

    private static EuroOfficeSaveCandidate Candidate(string uri) =>
        new(new string('a', 64), 2, new Uri(uri), new string('f', 64));

    [TestMethod]
    public void AcceptsOnlyOperatorPinnedHostnameAndIPAddress()
    {
        PinnedEuroOfficeDownloader.ValidateDestination(
            Candidate("https://office.get.intra/cache/revision.docx?auth=opaque"),
            Office, ApprovedIp);
        foreach (var address in new[] {
            "http://office.get.intra/cache/revision.docx",
            "https://external.invalid/cache/revision.docx",
            "https://user:password@office.get.intra/cache/revision.docx",
            "https://office.get.intra:8443/cache/revision.docx",
            "https://office.get.intra/cache/revision.docx#fragment"
        })
        {
            Assert.ThrowsExactly<ArgumentException>(() =>
                PinnedEuroOfficeDownloader.ValidateDestination(
                    Candidate(address), Office, ApprovedIp));
        }
    }

    [TestMethod]
    public void NeverAcceptsUnspecifiedAddressOrAnIpHostname()
    {
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            PinnedEuroOfficeDownloader.ValidateDestination(
                Candidate("https://office.get.intra/cache/file.docx"),
                Office, IPAddress.Any));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            PinnedEuroOfficeDownloader.ValidateDestination(
                Candidate("https://10.54.22.17/cache/file.docx"),
                new Uri("https://10.54.22.17/"), ApprovedIp));
    }

    [TestMethod]
    public async Task AcceptsOnlyCompleteByteExactFile()
    {
        var bytes = Encoding.ASCII.GetBytes("verified-office-payload");
        using var reply = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        reply.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/octet-stream");
        var result = await PinnedEuroOfficeDownloader.ReadAndHashAsync(reply);
        CollectionAssert.AreEqual(bytes, result.Bytes);
        Assert.AreEqual(
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            result.Sha256);
    }

    [TestMethod]
    public async Task ATruncatedResponseIsNotEverExposedToGup2()
    {
        var bytes = Encoding.ASCII.GetBytes("incomplete");
        using var reply = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        reply.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/octet-stream");
        reply.Content.Headers.ContentLength = bytes.Length + 100;
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() =>
            PinnedEuroOfficeDownloader.ReadAndHashAsync(reply));
    }

    [TestMethod]
    public async Task RedirectOrHtmlResponseFailsClosed()
    {
        using var redirect = new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Content = new ByteArrayContent(Encoding.ASCII.GetBytes("redirect"))
        };
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
            PinnedEuroOfficeDownloader.ReadAndHashAsync(redirect));

        using var html = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>credential prompt</html>")
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            PinnedEuroOfficeDownloader.ReadAndHashAsync(html));
    }

    [TestMethod]
    public async Task RefusesUnsupportedMimesAndOversizedLengths()
    {
        using var reply = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
        };
        reply.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/octet-stream");
        reply.Content.Headers.ContentLength = PinnedEuroOfficeDownloader.MaximumBytes + 1L;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            PinnedEuroOfficeDownloader.ReadAndHashAsync(reply));
    }
}
