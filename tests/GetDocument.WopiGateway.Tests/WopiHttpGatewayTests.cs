using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GetDocument.Collaboration;
using GetDocument.WopiGateway;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.WopiGateway.Tests;

[TestClass]
public sealed class WopiHttpGatewayTests
{
    private static readonly byte[] Secret = Enumerable.Repeat((byte)0x4B, 32).ToArray();
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 15, 0, 0, TimeSpan.Zero);
    private static readonly Uri Origin = new("https://wopi.get.test/");
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("CONTENT-42");
    private RSA _current = null!;
    private RSA _old = null!;
    private WopiGatewaySecurity _security = null!;
    private WopiDocument _doc = null!;
    private TestStore _store = null!;

    [TestInitialize]
    public void Init()
    {
        _current = RSA.Create(2048);
        _old = RSA.Create(2048);
        _security = new WopiGatewaySecurity(Secret, Origin,
            _current.ExportParameters(false), _old.ExportParameters(false));
        _doc = new WopiDocument("doc-42", "r1", "Приказ.docx",
            "get", "worker-1", "Сотрудник", Content.Length, CanEdit: true,
            IsWormOriginal: false);
        _store = new TestStore(_doc, Content);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _current.Dispose();
        _old.Dispose();
    }

    [TestMethod]
    public async Task CheckFileInfoRequiresSignedProofAndReturnsReadonlyJson()
    {
        var ctx = Context("/wopi/files/doc-42");
        await WopiHttpGateway.HandleCheckFileInfoAsync(ctx, "doc-42",
            _store, _security, TimeProviderForTest());
        Assert.AreEqual(200, ctx.Response.StatusCode);
        Assert.IsTrue(ctx.Response.ContentType?.StartsWith("application/json", StringComparison.Ordinal) ?? false);
        // Compare parsed values: System.Text.Json escapes Cyrillic as \uXXXX, which is valid JSON.
        using var payload = System.Text.Json.JsonDocument.Parse(((MemoryStream)ctx.Response.Body).ToArray());
        var info = payload.RootElement;
        Assert.AreEqual("Приказ.docx", info.GetProperty("BaseFileName").GetString());
        Assert.IsFalse(info.GetProperty("UserCanWrite").GetBoolean());
        Assert.IsTrue(info.GetProperty("UserCanNotWriteRelative").GetBoolean());
        Assert.AreEqual(1, _store.ResolveCount);
        Assert.AreEqual(0, _store.OpenCount);
    }

    [TestMethod]
    public async Task GetFileStreamsOnlyAuthorizedSnapshotAndAdvertisesVersion()
    {
        var ctx = Context("/wopi/files/doc-42/contents");
        await WopiHttpGateway.HandleGetFileAsync(ctx, "doc-42",
            _store, _security, TimeProviderForTest());
        Assert.AreEqual(200, ctx.Response.StatusCode);
        CollectionAssert.AreEqual(Content, ((MemoryStream)ctx.Response.Body).ToArray());
        Assert.AreEqual("r1", ctx.Response.Headers["X-WOPI-ItemVersion"].ToString());
        Assert.AreEqual((long)Content.Length, ctx.Response.ContentLength.GetValueOrDefault());
        Assert.AreEqual(1, _store.OpenCount);
    }

    [TestMethod]
    public async Task MissingOrTamperedProofNeverReachesStorage()
    {
        var ctx = Context("/wopi/files/doc-42");
        ctx.Request.Headers.Remove("X-WOPI-Proof");
        await WopiHttpGateway.HandleCheckFileInfoAsync(ctx, "doc-42",
            _store, _security, TimeProviderForTest());
        Assert.AreNotEqual(200, ctx.Response.StatusCode);
        Assert.AreEqual(0, _store.ResolveCount);
    }

    [TestMethod]
    public async Task WrongRevisionDoesNotOpenFile()
    {
        var ctx = Context("/wopi/files/doc-42/contents", revision: "r2");
        await WopiHttpGateway.HandleGetFileAsync(ctx, "doc-42",
            _store, _security, TimeProviderForTest());
        Assert.AreEqual(404, ctx.Response.StatusCode);
        Assert.AreEqual(0, _store.OpenCount);
    }

    [TestMethod]
    public async Task DenyAllProviderNeverReturnsDocument()
    {
        var ctx = Context("/wopi/files/doc-42");
        await WopiHttpGateway.HandleCheckFileInfoAsync(ctx, "doc-42",
            new DenyAllWopiDocumentStore(), _security, TimeProviderForTest());
        Assert.AreEqual(404, ctx.Response.StatusCode);
    }

    [TestMethod]
    public async Task RequestSizeLimitIsEnforcedBeforeFileIsOpened()
    {
        var ctx = Context("/wopi/files/doc-42/contents");
        ctx.Request.Headers["X-WOPI-MaxExpectedSize"] = "3";
        await WopiHttpGateway.HandleGetFileAsync(ctx, "doc-42",
            _store, _security, TimeProviderForTest());
        Assert.AreEqual(412, ctx.Response.StatusCode);
        Assert.AreEqual(0, _store.OpenCount);
    }

    [TestMethod]
    public async Task MissingTokenIsUnauthorizedWithoutMetadataLookup()
    {
        var ctx = Context("/wopi/files/doc-42");
        ctx.Request.QueryString = QueryString.Empty;
        await WopiHttpGateway.HandleCheckFileInfoAsync(ctx, "doc-42",
            _store, _security, TimeProviderForTest());
        Assert.AreEqual(401, ctx.Response.StatusCode);
        Assert.AreEqual(0, _store.ResolveCount);
    }

    [TestMethod]
    public async Task InvalidMaxExpectedSizeIsRejectedBeforeOpeningStream()
    {
        var ctx = Context("/wopi/files/doc-42/contents");
        ctx.Request.Headers["X-WOPI-MaxExpectedSize"] = "not-a-number";
        await WopiHttpGateway.HandleGetFileAsync(ctx, "doc-42",
            _store, _security, TimeProviderForTest());
        Assert.AreEqual(400, ctx.Response.StatusCode);
        Assert.AreEqual(0, _store.OpenCount);
    }

    [TestMethod]
    public async Task ExpiredTokenDoesNotReachDocumentStore()
    {
        var ctx = Context("/wopi/files/doc-42", at: Now.AddMinutes(-20));
        await WopiHttpGateway.HandleCheckFileInfoAsync(ctx, "doc-42",
            _store, _security, TimeProviderForTest());
        Assert.AreEqual(401, ctx.Response.StatusCode);
        Assert.AreEqual(0, _store.ResolveCount);
    }

    [TestMethod]
    public async Task UntrustedHostCannotBypassProofVerification()
    {
        var ctx = Context("/wopi/files/doc-42");
        ctx.Request.Host = new HostString("evil.get.test");
        await WopiHttpGateway.HandleCheckFileInfoAsync(ctx, "doc-42",
            _store, _security, TimeProviderForTest());
        Assert.AreNotEqual(200, ctx.Response.StatusCode);
        Assert.AreEqual(0, _store.ResolveCount);
    }

    [TestMethod]
    public async Task StaleOrWrongLengthContentIsNotServed()
    {
        _store = new TestStore(_doc with { FileSize = Content.Length + 1 }, Content);
        var ctx = Context("/wopi/files/doc-42/contents");
        await WopiHttpGateway.HandleGetFileAsync(ctx, "doc-42",
            _store, _security, TimeProviderForTest());
        Assert.AreEqual(409, ctx.Response.StatusCode);
        Assert.AreEqual(0, ((MemoryStream)ctx.Response.Body).Length);
    }

    [TestMethod]
    public async Task SameLengthTamperingIsRejectedBeforeWritingAnyBytes()
    {
        _store.CorruptSameLengthBytes = true;
        var ctx = Context("/wopi/files/doc-42/contents");
        await WopiHttpGateway.HandleGetFileAsync(
            ctx, "doc-42", _store, _security, TimeProviderForTest());
        Assert.AreEqual(409, ctx.Response.StatusCode);
        Assert.AreEqual(0, ((MemoryStream)ctx.Response.Body).Length);
    }

    [TestMethod]
    public async Task RevokedAclIsCheckedAgainForNextRequest()
    {
        var before = Context("/wopi/files/doc-42");
        await WopiHttpGateway.HandleCheckFileInfoAsync(
            before, "doc-42", _store, _security, TimeProviderForTest());
        Assert.AreEqual(200, before.Response.StatusCode);
        _store.AllowRead = false;
        var after = Context("/wopi/files/doc-42/contents");
        await WopiHttpGateway.HandleGetFileAsync(
            after, "doc-42", _store, _security, TimeProviderForTest());
        Assert.AreEqual(404, after.Response.StatusCode);
        Assert.AreEqual(0, _store.OpenCount);
        Assert.AreEqual(2, _store.ResolveCount);
    }

    private TimeProvider TimeProviderForTest() => new FixedClock(Now);

    private DefaultHttpContext Context(string path, string revision = "r1", DateTimeOffset? at = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("wopi.get.test");
        ctx.Request.Path = new PathString(path);
        var metadata = _doc with { RevisionId = revision };
        var issued = WopiReadOnlySessionService.Issue(metadata, Secret,
            at ?? Now, TimeSpan.FromMinutes(5));
        ctx.Request.QueryString = new QueryString("?access_token=" + Uri.EscapeDataString(issued.AccessToken));
        long ticks = Now.UtcDateTime.Ticks;
        string full = Origin.GetLeftPart(UriPartial.Authority) + path + ctx.Request.QueryString;
        byte[] payload = WopiProofValidator.BuildExpectedProof(issued.AccessToken,
            new Uri(full), ticks);
        ctx.Request.Headers["X-WOPI-TimeStamp"] = ticks.ToString(CultureInfo.InvariantCulture);
        ctx.Request.Headers["X-WOPI-Proof"] = Convert.ToBase64String(_current.SignData(
            payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private sealed class FixedClock(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
    }

    private sealed class TestStore(WopiDocument doc, byte[] data) : ITrustedWopiDocumentStore
    {
        public int ResolveCount { get; private set; }
        public int OpenCount { get; private set; }
        public bool AllowRead { get; set; } = true;
        public bool CorruptSameLengthBytes { get; set; }

        public ValueTask<WopiAuthorizedContent?> ResolveForReadAsync(
            string docId, string revisionId, string userId, CancellationToken ct)
        {
            ResolveCount++;
            if (!AllowRead || doc.DocumentId != docId || doc.RevisionId != revisionId || doc.UserId != userId)
                return ValueTask.FromResult<WopiAuthorizedContent?>(null);
            return ValueTask.FromResult<WopiAuthorizedContent?>(new WopiAuthorizedContent(
                doc, Convert.ToHexString(SHA256.HashData(data)), _ =>
                {
                    OpenCount++;
                    byte[] delivered = (byte[])data.Clone();
                    if (CorruptSameLengthBytes) delivered[0] ^= 0x7F;
                    return ValueTask.FromResult<Stream>(new MemoryStream(delivered, writable: false));
                }));
        }
    }
}
