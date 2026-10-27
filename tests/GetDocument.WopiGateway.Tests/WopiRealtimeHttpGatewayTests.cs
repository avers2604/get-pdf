using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GetDocument.Collaboration;
using GetDocument.WopiGateway;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.WopiGateway.Tests;

[TestClass]
public sealed class WopiRealtimeHttpGatewayTests
{
    private static readonly byte[] Secret = Enumerable.Repeat((byte)0x45, 32).ToArray();
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Origin = new("https://wopi.get.test/");
    private RSA _proof = null!;
    private RSA _old = null!;
    private WopiGatewaySecurity _security = null!;
    private LiveTestStore _store = null!;
    private string _folder = null!;
    private StoredOfficeRevision _initial = null!;

    [TestInitialize]
    public async Task SetupAsync()
    {
        _proof = RSA.Create(2048);
        _old = RSA.Create(2048);
        _security = new WopiGatewaySecurity(
            Secret, Origin, _proof.ExportParameters(false), _old.ExportParameters(false));
        _folder = Path.Combine(Path.GetTempPath(), "gd09f-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        var revisions = new ImmutableOfficeRevisionStore(_folder);
        await using var source = Data("ORIGINAL WORM SAFE");
        _initial = await revisions.InitializeAsync("doc-42", ".docx", source);
        _store = new LiveTestStore(revisions, Now);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _proof.Dispose();
        _old.Dispose();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [TestMethod]
    public async Task TwoEditorsHaveSameRoomButDifferentTokensAndBothReceiveEditableMetadata()
    {
        var a = Context("alice", "/wopi/files/doc-42");
        var b = Context("bob", "/wopi/files/doc-42");
        await WopiLiveHttpGateway.HandleCheckFileInfoAsync(a, "doc-42",
            _store, _security, new FixedClock(Now));
        await WopiLiveHttpGateway.HandleCheckFileInfoAsync(b, "doc-42",
            _store, _security, new FixedClock(Now));
        Assert.AreEqual(200, a.Response.StatusCode);
        Assert.AreEqual(200, b.Response.StatusCode);
        using var ja = JsonDocument.Parse(Response(a));
        using var jb = JsonDocument.Parse(Response(b));
        Assert.IsTrue(ja.RootElement.GetProperty("UserCanWrite").GetBoolean());
        Assert.IsTrue(jb.RootElement.GetProperty("UserCanWrite").GetBoolean());
        Assert.AreEqual(_initial.RevisionId,
            ja.RootElement.GetProperty("Version").GetString());
        Assert.AreEqual(ja.RootElement.GetProperty("Version").GetString(),
            jb.RootElement.GetProperty("Version").GetString());
        Assert.AreNotEqual(a.Request.QueryString, b.Request.QueryString);
    }

    [TestMethod]
    public async Task SignedGetFileReadsOnlyHashedImmutableSnapshot()
    {
        var ctx = Context("alice", "/wopi/files/doc-42/contents");
        await WopiLiveHttpGateway.HandleGetFileAsync(ctx, "doc-42",
            _store, _security, new FixedClock(Now));
        Assert.AreEqual(200, ctx.Response.StatusCode);
        Assert.AreEqual("ORIGINAL WORM SAFE", Response(ctx));
        Assert.AreEqual(_initial.RevisionId, ctx.Response.Headers["X-WOPI-ItemVersion"].ToString());
    }

    [TestMethod]
    public async Task AuthorizedPutFilePublishesNewVersionButNeverOverwritesOriginal()
    {
        var put = Context("alice", "/wopi/files/doc-42/contents",
            content: Encoding.UTF8.GetBytes("ALICE AND BOB LIVE CONTENT"));
        put.Request.Headers["X-WOPI-Override"] = "PUT";
        put.Request.Headers["X-COOL-WOPI-Timestamp"] = _initial.LastModifiedUtc.ToString("O");
        await WopiLiveHttpGateway.HandlePutFileAsync(put, "doc-42",
            _store, _security, new FixedClock(Now));
        Assert.AreEqual(200, put.Response.StatusCode);
        var latest = _store.Revisions.ReadCurrent("doc-42");
        Assert.AreNotEqual(_initial.RevisionId, latest.RevisionId);
        Assert.AreEqual("ORIGINAL WORM SAFE", File.ReadAllText(_initial.FilePath));
        Assert.AreEqual("ALICE AND BOB LIVE CONTENT", File.ReadAllText(latest.FilePath));
        Assert.AreEqual(latest.RevisionId,
            put.Response.Headers["X-WOPI-ItemVersion"].ToString());
        using var success = JsonDocument.Parse(Response(put));
        Assert.IsTrue(success.RootElement.TryGetProperty("LastModifiedTime", out _));
    }

    [TestMethod]
    public async Task StaleSecondEditorGets409Cool1010WithoutOverwritingFirst()
    {
        var first = Context("alice", "/wopi/files/doc-42/contents", content: Encoding.UTF8.GetBytes("FIRST"));
        first.Request.Headers["X-WOPI-Override"] = "PUT";
        first.Request.Headers["X-COOL-WOPI-Timestamp"] = _initial.LastModifiedUtc.ToString("O");
        await WopiLiveHttpGateway.HandlePutFileAsync(first, "doc-42",
            _store, _security, new FixedClock(Now));
        Assert.AreEqual(200, first.Response.StatusCode);
        var newest = _store.Revisions.ReadCurrent("doc-42");

        var stale = Context("bob", "/wopi/files/doc-42/contents", content: Encoding.UTF8.GetBytes("STALE"));
        stale.Request.Headers["X-WOPI-Override"] = "PUT";
        stale.Request.Headers["X-COOL-WOPI-Timestamp"] = _initial.LastModifiedUtc.ToString("O");
        await WopiLiveHttpGateway.HandlePutFileAsync(stale, "doc-42",
            _store, _security, new FixedClock(Now));
        Assert.AreEqual(409, stale.Response.StatusCode);
        StringAssert.Contains(Response(stale), "\"COOLStatusCode\":1010");
        Assert.AreEqual(newest.RevisionId, _store.Revisions.ReadCurrent("doc-42").RevisionId);
        Assert.AreEqual("FIRST", File.ReadAllText(newest.FilePath));
    }

    [TestMethod]
    public async Task MissingTimestampCannotOverrideConflictingRevision()
    {
        var put = Context("alice", "/wopi/files/doc-42/contents", content: Encoding.UTF8.GetBytes("NO TIMESTAMP"));
        put.Request.Headers["X-WOPI-Override"] = "PUT";
        await WopiLiveHttpGateway.HandlePutFileAsync(put, "doc-42",
            _store, _security, new FixedClock(Now));
        Assert.AreEqual(409, put.Response.StatusCode);
        Assert.AreEqual(_initial.RevisionId, _store.Revisions.ReadCurrent("doc-42").RevisionId);
    }

    [TestMethod]
    public async Task WormOnlyGetsViewerMetadataAndCannotPut()
    {
        _store.IsWorm = true;
        var view = Context("alice", "/wopi/files/doc-42");
        await WopiLiveHttpGateway.HandleCheckFileInfoAsync(view, "doc-42",
            _store, _security, new FixedClock(Now));
        Assert.AreEqual(200, view.Response.StatusCode);
        StringAssert.Contains(Response(view), "\"UserCanWrite\":false");

        var put = Context("alice", "/wopi/files/doc-42/contents", content: Encoding.UTF8.GetBytes("BAD"));
        put.Request.Headers["X-WOPI-Override"] = "PUT";
        put.Request.Headers["X-COOL-WOPI-Timestamp"] = _initial.LastModifiedUtc.ToString("O");
        await WopiLiveHttpGateway.HandlePutFileAsync(put, "doc-42",
            _store, _security, new FixedClock(Now));
        Assert.AreEqual(403, put.Response.StatusCode);
        Assert.AreEqual(0, _store.CommitCount);
    }

    [TestMethod]
    public async Task TamperedProofNeverReachesAclStore()
    {
        var request = Context("alice", "/wopi/files/doc-42/contents");
        request.Request.Headers["X-WOPI-Proof"] = "bad";
        await WopiLiveHttpGateway.HandleGetFileAsync(request, "doc-42",
            _store, _security, new FixedClock(Now));
        Assert.AreEqual(403, request.Response.StatusCode);
        Assert.AreEqual(0, _store.ResolveCount);
    }

    [TestMethod]
    public async Task ExpiredTokenAndRevokedAclFailClosed()
    {
        var expired = Context("alice", "/wopi/files/doc-42", issuedAt: Now.AddMinutes(-10));
        await WopiLiveHttpGateway.HandleCheckFileInfoAsync(expired, "doc-42",
            _store, _security, new FixedClock(Now));
        Assert.AreEqual(401, expired.Response.StatusCode);
        Assert.AreEqual(0, _store.ResolveCount);

        _store.AllowAccess = false;
        var blocked = Context("alice", "/wopi/files/doc-42");
        await WopiLiveHttpGateway.HandleCheckFileInfoAsync(blocked, "doc-42",
            _store, _security, new FixedClock(Now));
        Assert.AreEqual(404, blocked.Response.StatusCode);
    }

    [TestMethod]
    public async Task OversizedRequestIsRejectedBeforeCommit()
    {
        var ctx = Context("alice", "/wopi/files/doc-42/contents",
            content: Encoding.UTF8.GetBytes("not relevant"));
        ctx.Request.ContentLength = 40L * 1024 * 1024;
        ctx.Request.Headers["X-WOPI-Override"] = "PUT";
        ctx.Request.Headers["X-COOL-WOPI-Timestamp"] = _initial.LastModifiedUtc.ToString("O");
        await WopiLiveHttpGateway.HandlePutFileAsync(ctx, "doc-42",
            _store, _security, new FixedClock(Now));
        Assert.AreEqual(413, ctx.Response.StatusCode);
        Assert.AreEqual(0, _store.CommitCount);
    }

    [TestMethod]
    public async Task RevokingAclWhileUploadingFailsBeforeCommit()
    {
        var bytes = Encoding.UTF8.GetBytes("NOW FORBIDDEN");
        var ctx = Context("alice", "/wopi/files/doc-42/contents", content: bytes);
        ctx.Request.Headers["X-WOPI-Override"] = "PUT";
        ctx.Request.Headers["X-COOL-WOPI-Timestamp"] = _initial.LastModifiedUtc.ToString("O");
        ctx.Request.Body = new RevokeOnFirstReadStream(bytes, () => _store.AllowAccess = false);
        await WopiLiveHttpGateway.HandlePutFileAsync(ctx, "doc-42",
            _store, _security, new FixedClock(Now));
        Assert.AreEqual(404, ctx.Response.StatusCode);
        Assert.AreEqual(0, _store.CommitCount);
        Assert.AreEqual(_initial.RevisionId, _store.Revisions.ReadCurrent("doc-42").RevisionId);
    }

    [TestMethod]
    public async Task WrongWopiOperationDoesNotSave()
    {
        var ctx = Context("alice", "/wopi/files/doc-42/contents", content: Encoding.UTF8.GetBytes("BAD"));
        ctx.Request.Headers["X-WOPI-Override"] = "LOCK";
        ctx.Request.Headers["X-COOL-WOPI-Timestamp"] = _initial.LastModifiedUtc.ToString("O");
        await WopiLiveHttpGateway.HandlePutFileAsync(ctx, "doc-42",
            _store, _security, new FixedClock(Now));
        Assert.AreEqual(400, ctx.Response.StatusCode);
        Assert.AreEqual(0, _store.CommitCount);
    }

    [TestMethod]
    public async Task SignedCollaboraPostWithoutOverrideIsAcceptedWhenTimestampMatches()
    {
        var ctx = Context("alice", "/wopi/files/doc-42/contents",
            content: Encoding.UTF8.GetBytes("COLLABORA POST"));
        // Collabora's WOPI-like PutFile uses POST /contents; unlike the
        // Microsoft WOPI profile, X-WOPI-Override may be omitted.
        ctx.Request.Headers["X-COOL-WOPI-Timestamp"] = _initial.LastModifiedUtc.ToString("O");
        await WopiLiveHttpGateway.HandlePutFileAsync(ctx, "doc-42",
            _store, _security, new FixedClock(Now));
        Assert.AreEqual(200, ctx.Response.StatusCode);
        Assert.AreEqual("COLLABORA POST",
            File.ReadAllText(_store.Revisions.ReadCurrent("doc-42").FilePath));
    }

    [TestMethod]
    public async Task ChunkedUploadWithoutContentLengthIsBoundedAndCanSave()
    {
        var ctx = Context("bob", "/wopi/files/doc-42/contents",
            content: Encoding.UTF8.GetBytes("CHUNKED COMPLETE"));
        ctx.Request.ContentLength = null;
        ctx.Request.Headers["X-COOL-WOPI-Timestamp"] = _initial.LastModifiedUtc.ToString("O");
        await WopiLiveHttpGateway.HandlePutFileAsync(ctx, "doc-42",
            _store, _security, new FixedClock(Now));
        Assert.AreEqual(200, ctx.Response.StatusCode);
        Assert.AreEqual("CHUNKED COMPLETE",
            File.ReadAllText(_store.Revisions.ReadCurrent("doc-42").FilePath));
    }

    [TestMethod]
    public async Task DefaultDenyAllRealtimeStoreNeverExposesDocument()
    {
        var ctx = Context("alice", "/wopi/files/doc-42");
        await WopiLiveHttpGateway.HandleCheckFileInfoAsync(ctx, "doc-42",
            new DenyAllRealtimeWopiDocumentStore(), _security, new FixedClock(Now));
        Assert.AreEqual(404, ctx.Response.StatusCode);
    }

    [TestMethod]
    public async Task RevokingEditPermissionAfterTokenIssueBlocksSave()
    {
        var ctx = Context("alice", "/wopi/files/doc-42/contents",
            content: Encoding.UTF8.GetBytes("REVOKED"));
        _store.AllowEdit = false;
        ctx.Request.Headers["X-WOPI-Override"] = "PUT";
        ctx.Request.Headers["X-COOL-WOPI-Timestamp"] = _initial.LastModifiedUtc.ToString("O");
        await WopiLiveHttpGateway.HandlePutFileAsync(ctx, "doc-42",
            _store, _security, new FixedClock(Now));
        Assert.AreNotEqual(200, ctx.Response.StatusCode);
        Assert.AreEqual(0, _store.CommitCount);
        Assert.AreEqual(_initial.RevisionId, _store.Revisions.ReadCurrent("doc-42").RevisionId);
    }

    [TestMethod]
    public async Task RejectedOfficePackageReturns422AndDoesNotClaimSavedRevision()
    {
        _store.RejectOfficeZip = true;
        var ctx = Context("alice", "/wopi/files/doc-42/contents",
            content: Encoding.UTF8.GetBytes("fake office document"));
        ctx.Request.Headers["X-COOL-WOPI-Timestamp"] = _initial.LastModifiedUtc.ToString("O");
        await WopiLiveHttpGateway.HandlePutFileAsync(ctx, "doc-42",
            _store, _security, new FixedClock(Now));
        Assert.AreEqual(422, ctx.Response.StatusCode);
        Assert.AreEqual(_initial.RevisionId,
            _store.Revisions.ReadCurrent("doc-42").RevisionId);
        Assert.IsFalse(ctx.Response.Headers.ContainsKey("X-WOPI-ItemVersion"));
    }

    [TestMethod]
    public async Task SameLengthTamperedSnapshotIsNotServed()
    {
        _store.TamperRead = true;
        var ctx = Context("alice", "/wopi/files/doc-42/contents");
        await WopiLiveHttpGateway.HandleGetFileAsync(ctx, "doc-42",
            _store, _security, new FixedClock(Now));
        Assert.AreEqual(409, ctx.Response.StatusCode);
        Assert.AreEqual(0, ((MemoryStream)ctx.Response.Body).Length);
    }

    private DefaultHttpContext Context(string user, string path,
        byte[]? content = null, DateTimeOffset? issuedAt = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("wopi.get.test");
        ctx.Request.Path = path;
        var metadata = _store.Metadata(user);
        var ticket = RealtimeCoauthoring.Issue(metadata, Secret,
            issuedAt ?? Now, TimeSpan.FromMinutes(5), editingGatewayReady: true);
        ctx.Request.QueryString = new QueryString(
            "?access_token=" + Uri.EscapeDataString(ticket.AccessToken));
        long timestamp = Now.UtcDateTime.Ticks;
        string full = Origin.GetLeftPart(UriPartial.Authority) + path + ctx.Request.QueryString;
        byte[] proof = WopiProofValidator.BuildExpectedProof(
            ticket.AccessToken, new Uri(full), timestamp);
        ctx.Request.Headers["X-WOPI-TimeStamp"] = timestamp.ToString(CultureInfo.InvariantCulture);
        ctx.Request.Headers["X-WOPI-Proof"] = Convert.ToBase64String(_proof.SignData(
            proof, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        ctx.Request.Body = new MemoryStream(content ?? []);
        ctx.Request.ContentLength = content?.Length;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private sealed class RevokeOnFirstReadStream(byte[] bytes, Action revoke)
        : MemoryStream(bytes, writable: false)
    {
        private bool _revoked;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_revoked)
            {
                _revoked = true;
                revoke();
            }
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private static MemoryStream Data(string text) => new(Encoding.UTF8.GetBytes(text));
    private static string Response(DefaultHttpContext ctx) =>
        Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class LiveTestStore(
        ImmutableOfficeRevisionStore store, DateTimeOffset now) : ITrustedRealtimeWopiDocumentStore
    {
        public ImmutableOfficeRevisionStore Revisions { get; } = store;
        public int ResolveCount { get; private set; }
        public int CommitCount { get; private set; }
        public bool AllowAccess { get; set; } = true;
        public bool IsWorm { get; set; }
        public bool AllowEdit { get; set; } = true;
        public bool TamperRead { get; set; }
        public bool RejectOfficeZip { get; set; }

        public WopiDocument Metadata(string user)
        {
            var current = store.ReadCurrent("doc-42");
            return new WopiDocument("doc-42", "session-base-r1", "Приказ.docx",
                "get", user, user, current.Length, AllowEdit, IsWorm);
        }

        public ValueTask<WopiRealtimeAuthorizedContent?> ResolveForSessionAsync(
            string documentId, string sessionRevisionId, string userId, CancellationToken ct)
        {
            ResolveCount++;
            if (!AllowAccess || documentId != "doc-42" || sessionRevisionId != "session-base-r1" ||
                userId is not ("alice" or "bob"))
                return ValueTask.FromResult<WopiRealtimeAuthorizedContent?>(null);
            var revision = store.ReadCurrent(documentId);
            var doc = Metadata(userId);
            return ValueTask.FromResult<WopiRealtimeAuthorizedContent?>(new(
                doc, revision,
                _ =>
                {
                    if (!TamperRead) return ValueTask.FromResult<Stream>(
                        File.OpenRead(revision.FilePath));
                    byte[] bytes = File.ReadAllBytes(revision.FilePath);
                    bytes[0] ^= 0x01;
                    return ValueTask.FromResult<Stream>(
                        new MemoryStream(bytes, writable: false));
                }));
        }

        public async ValueTask<StoredOfficeRevision> CommitNewRevisionAsync(
            WopiRealtimeAuthorizedContent authorized, RealtimeCoauthoringGrant grant,
            string? timestamp, Stream content, CancellationToken ct)
        {
            CommitCount++;
            if (RejectOfficeZip)
                throw new RejectedOfficePackageException("Недопустимый OOXML пакет.");
            return await RealtimeRevisionCommit.CommitAsync(store,
                authorized.Document, grant, now, timestamp, content, ct);
        }
    }
}
