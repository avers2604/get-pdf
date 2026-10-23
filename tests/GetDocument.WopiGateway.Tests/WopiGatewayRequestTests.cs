using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GetDocument.Collaboration;
using GetDocument.WopiGateway;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.WopiGateway.Tests;

[TestClass]
public sealed class WopiGatewayRequestTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task SignedCheckFileInfoRespondsWithMetadataWithoutLeakingJwt()
    {
        using var fixture = new Fixture();
        var result = await fixture.RequestAsync(contents: false);
        Assert.AreEqual(200, result.Status);
        using var json = JsonDocument.Parse(result.Body);
        Assert.AreEqual("Приказ.docx", json.RootElement.GetProperty("BaseFileName").GetString());
        Assert.IsFalse(json.RootElement.GetProperty("UserCanWrite").GetBoolean());
        Assert.IsFalse(json.RootElement.GetProperty("SupportsUpdate").GetBoolean());
        Assert.IsFalse(json.RootElement.GetProperty("SupportsLocks").GetBoolean());
        Assert.IsFalse(result.Body.Contains("access_token", StringComparison.Ordinal));
        Assert.AreEqual(0, fixture.Store.OpenCount);
        Assert.AreEqual(1, fixture.Authorization.CallCount);
    }

    [TestMethod]
    public async Task SignedGetFileStreamsVerifiedExactBytesWithoutWrites()
    {
        using var fixture = new Fixture();
        var result = await fixture.RequestAsync(contents: true);
        Assert.AreEqual(200, result.Status);
        CollectionAssert.AreEqual(fixture.File, result.Raw);
        Assert.AreEqual("application/octet-stream", result.ContentType);
        Assert.AreEqual(1, fixture.Store.OpenCount);
        Assert.AreEqual(1, fixture.Authorization.CallCount);
    }

    [TestMethod]
    public async Task NoOrTamperedProofFailsClosedWithoutOpeningStorage()
    {
        using var fixture = new Fixture();
        Assert.AreEqual(500, (await fixture.RequestAsync(contents: true, proofMode: "missing")).Status);
        Assert.AreEqual(500, (await fixture.RequestAsync(contents: true, proofMode: "tampered")).Status);
        Assert.AreEqual(0, fixture.Store.OpenCount);
    }

    [TestMethod]
    public async Task NoOrInvalidBearerTokenFailsClosed()
    {
        using var fixture = new Fixture();
        Assert.AreEqual(401, (await fixture.RequestAsync(contents: true, tokenMode: "missing")).Status);
        Assert.AreEqual(401, (await fixture.RequestAsync(contents: true, tokenMode: "tampered")).Status);
        Assert.AreEqual(0, fixture.Store.OpenCount);
    }

    [TestMethod]
    public async Task AclRejectionOrMissingRevisionReturns404WithoutOpeningContent()
    {
        using var fixture = new Fixture();
        fixture.Authorization.Allow = false;
        Assert.AreEqual(404, (await fixture.RequestAsync(contents: true)).Status);
        fixture.Authorization.Allow = true;
        fixture.Store.Exists = false;
        Assert.AreEqual(404, (await fixture.RequestAsync(contents: true)).Status);
        Assert.AreEqual(0, fixture.Store.OpenCount);
    }

    [TestMethod]
    public async Task VersionHashMismatchNeverPublishesFile()
    {
        using var fixture = new Fixture();
        fixture.Store.ReturnCorruptedBytes = true;
        var result = await fixture.RequestAsync(contents: true);
        Assert.AreEqual(409, result.Status);
        Assert.AreEqual(0, result.Raw.Length);
    }

    [TestMethod]
    public async Task WrongTlsHostAndPathCannotBypassOriginChecks()
    {
        using var fixture = new Fixture();
        Assert.AreEqual(400, (await fixture.RequestAsync(contents: true, scheme: "http")).Status);
        Assert.AreEqual(400, (await fixture.RequestAsync(contents: true, host: "external.example")).Status);
        Assert.AreEqual(404, (await fixture.RequestAsync(contents: true, path: "/wopi/files/wrong/contents")).Status);
        Assert.AreEqual(0, fixture.Store.OpenCount);
    }

    [TestMethod]
    public async Task SizeAboveClientLimitReturnsPreconditionFailed()
    {
        using var fixture = new Fixture();
        var result = await fixture.RequestAsync(contents: true, maxExpectedSize: 3);
        Assert.AreEqual(412, result.Status);
        Assert.AreEqual(0, fixture.Store.OpenCount);
    }

    [TestMethod]
    public async Task ExpiredWopiSessionNeverStreamsBytes()
    {
        using var fixture = new Fixture();
        var result = await fixture.RequestAsync(contents: true, now: Now.AddMinutes(6));
        Assert.AreEqual(401, result.Status);
        Assert.AreEqual(0, fixture.Store.OpenCount);
    }

    [TestMethod]
    public async Task AclMustBeEvaluatedForEveryIndependentRequest()
    {
        using var fixture = new Fixture();
        Assert.AreEqual(200, (await fixture.RequestAsync(contents: false)).Status);
        fixture.Authorization.Allow = false;
        Assert.AreEqual(404, (await fixture.RequestAsync(contents: true)).Status);
        Assert.AreEqual(2, fixture.Authorization.CallCount);
        Assert.AreEqual(0, fixture.Store.OpenCount);
    }

    [TestMethod]
    public async Task WopiDocumentCannotAdvertisePutFileOrLockEndpoints()
    {
        using var fixture = new Fixture();
        var result = await fixture.RequestAsync(contents: false);
        using var json = JsonDocument.Parse(result.Body);
        Assert.IsFalse(json.RootElement.GetProperty("SupportsLocks").GetBoolean());
        Assert.IsFalse(json.RootElement.GetProperty("UserCanWrite").GetBoolean());
        Assert.IsFalse(json.RootElement.GetProperty("SupportsUpdate").GetBoolean());
    }

    private sealed record Result(int Status, string Body, byte[] Raw, string? ContentType);

    private sealed class Fixture : IDisposable
    {
        private readonly RSA _current = RSA.Create(2048);
        private readonly RSA _old = RSA.Create(2048);
        private readonly byte[] _secret = Enumerable.Repeat((byte)42, 32).ToArray();
        private readonly WopiReadOnlyGatewayOptions _options;
        public byte[] File { get; } = Encoding.UTF8.GetBytes("test-document-payload");
        public FixtureStore Store { get; }
        public FixtureAcl Authorization { get; } = new();

        public Fixture()
        {
            Store = new FixtureStore(File);
            _options = new WopiReadOnlyGatewayOptions(
                new Uri("https://wopi.get.intra/"), _secret,
                _current.ExportParameters(false), _old.ExportParameters(false));
        }

        public async Task<Result> RequestAsync(
            bool contents, string proofMode = "valid", string tokenMode = "valid",
            string scheme = "https", string host = "wopi.get.intra",
            string? path = null, long? maxExpectedSize = null, DateTimeOffset? now = null)
        {
            var source = new WopiDocument(
                "doc-42", "v1", "Приказ.docx", "get", "employee-1", "Сотрудник",
                File.Length, CanEdit: false, IsWormOriginal: true);
            string token = WopiReadOnlySessionService.Issue(
                source, _secret, Now, TimeSpan.FromMinutes(5)).AccessToken;
            if (tokenMode == "tampered") token += "x";
            if (tokenMode == "missing") token = "";
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
            context.RequestServices = services;
            context.Request.Scheme = scheme;
            context.Request.Host = new HostString(host);
            context.Request.Path = path ?? (contents
                ? "/wopi/files/doc-42/contents" : "/wopi/files/doc-42");
            if (token.Length > 0)
                context.Request.QueryString = QueryString.Create("access_token", token);
            if (maxExpectedSize is { } clientMax)
                context.Request.Headers["X-WOPI-MaxExpectedSize"] = clientMax.ToString();
            long ticks = Now.UtcDateTime.Ticks;
            context.Request.Headers["X-WOPI-TimeStamp"] = ticks.ToString();
            if (proofMode != "missing")
            {
                var expectedUrl = new Uri("https://wopi.get.intra" +
                    context.Request.Path.ToUriComponent() + context.Request.QueryString.ToUriComponent());
                byte[] bytes = WopiProofValidator.BuildExpectedProof(token, expectedUrl, ticks);
                byte[] sig = _current.SignData(bytes,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                if (proofMode == "tampered") sig[0] ^= 0xFF;
                context.Request.Headers["X-WOPI-Proof"] = Convert.ToBase64String(sig);
            }
            var result = await WopiReadOnlyGateway.HandleAsync(
                context, "doc-42", contents, _options, Store, Authorization,
                now ?? Now, CancellationToken.None);
            await result.ExecuteAsync(context);
            var body = ((MemoryStream)context.Response.Body).ToArray();
            return new Result(context.Response.StatusCode, Encoding.UTF8.GetString(body),
                body, context.Response.ContentType);
        }

        public void Dispose() { _current.Dispose(); _old.Dispose(); }
    }

    private sealed class FixtureStore : IWopiReadOnlyStore
    {
        private readonly byte[] _content;
        public int OpenCount { get; private set; }
        public bool ReturnCorruptedBytes { get; set; }
        public bool Exists { get; set; } = true;

        public FixtureStore(byte[] content) { _content = content; }

        public ValueTask<WopiStoredRevision?> FindAsync(
            string documentId, string revisionId, CancellationToken cancellationToken)
        {
            if (!Exists) return ValueTask.FromResult<WopiStoredRevision?>(null);
            return ValueTask.FromResult<WopiStoredRevision?>(new(
                documentId, revisionId, "Приказ.docx", "get", _content.LongLength,
                Convert.ToHexString(SHA256.HashData(_content)), IsWormOriginal: true));
        }

        public ValueTask<Stream> OpenReadAsync(
            WopiStoredRevision revision, CancellationToken cancellationToken)
        {
            OpenCount++;
            byte[] bytes = ReturnCorruptedBytes
                ? Encoding.UTF8.GetBytes("test-document-corrupted") : _content;
            return ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
    }

    private sealed class FixtureAcl : IWopiReadAuthorization
    {
        public bool Allow { get; set; } = true;
        public int CallCount { get; private set; }
        public ValueTask<WopiAuthorizedUser?> CheckReadAsync(
            string userId, WopiStoredRevision rev, CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult<WopiAuthorizedUser?>(
                Allow ? new WopiAuthorizedUser(userId, "Сотрудник") : null);
        }
    }
}
