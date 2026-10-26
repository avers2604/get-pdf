using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GetDocument.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

[TestClass]
public sealed class EuroOfficeLaunchPageTests
{
    private static readonly byte[] Key = SHA256.HashData(
        Encoding.UTF8.GetBytes("get-documents-euro-office-broker-test-secret"));
    private static readonly Uri OfficeOrigin = new("https://office.intra.get.test/");
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static EuroOfficeAuthorizedLaunch Grant(
        string title = "Приказ.docx", bool worm = false, bool backendReady = false) =>
        new(new OnlyOfficeSessionOptions(
            "doc-42", "rev-1", title, "user-17", "Сотрудник ГЭТ",
            new Uri("https://files.intra.get.test/"),
            new Uri("https://files.intra.get.test/documents/42/content"),
            new Uri("https://files.intra.get.test/documents/42/callback"),
            OfficeOrigin, true, worm),
            ExpiresAtUtc: Now.AddMinutes(2),
            ImmutableRevisionSinkReady: backendReady);

    [TestMethod]
    public void RendersSameOriginEditorWithStrictCspAndWithoutBearerInUrl()
    {
        var page = EuroOfficeLaunchPage.Render(Grant(), OfficeOrigin, Key, Now);
        StringAssert.Contains(page.Html, "https://office.intra.get.test/web-apps/apps/api/documents/api.js");
        StringAssert.Contains(page.Html, "new DocsAPI.DocEditor");
        StringAssert.Contains(page.Html, "JSON.parse(");
        StringAssert.Contains(page.ContentSecurityPolicy, "default-src 'none'");
        StringAssert.Contains(page.ContentSecurityPolicy, "frame-src https://office.intra.get.test");
        StringAssert.Contains(page.ContentSecurityPolicy, "script-src 'nonce-");
        Assert.IsFalse(page.Html.Contains("smb://", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(page.Html.Contains("access_token=", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(page.ContentSecurityPolicy.Contains("script-src 'unsafe-inline'", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void EncodesDocumentTitleSoItCannotBreakInlineScript()
    {
        var title = "<img src=x onerror=alert(1)>.docx";
        var page = EuroOfficeLaunchPage.Render(Grant(title), OfficeOrigin, Key, Now);
        Assert.IsFalse(page.Html.Contains("<img", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(page.Html.Contains("</script><script>", StringComparison.OrdinalIgnoreCase));
        // A signed editor config still contains the original Unicode title.
        var bootstrap = OfficeServerEnginePolicy.Create(Grant(title).Session, Key);
        using var parsed = JsonDocument.Parse(bootstrap.ConfigurationJson);
        Assert.AreEqual(title, parsed.RootElement.GetProperty("document").GetProperty("title").GetString());
    }

    [TestMethod]
    public void ExpiredOrOverlyLongOneTimeLaunchIsDenied()
    {
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => EuroOfficeLaunchPage.Render(
            Grant() with { ExpiresAtUtc = Now }, OfficeOrigin, Key, Now));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => EuroOfficeLaunchPage.Render(
            Grant() with { ExpiresAtUtc = Now.AddMinutes(10) }, OfficeOrigin, Key, Now));
    }

    [TestMethod]
    public void MismatchedOfficeHostOrHttpOriginIsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => EuroOfficeLaunchPage.Render(
            Grant(), new Uri("https://other.intra.get.test/"), Key, Now));
        Assert.ThrowsExactly<ArgumentException>(() => EuroOfficeLaunchPage.Render(
            Grant(), new Uri("http://office.intra.get.test/"), Key, Now));
    }

    [TestMethod]
    public void MissingSignedSessionSecretFailsClosed()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            EuroOfficeLaunchPage.Render(Grant(), OfficeOrigin, new byte[8], Now));
    }

    [TestMethod]
    public void Gup2WormRemainsReadOnlyEvenWhenRevisionSinkClaimsReady()
    {
        var session = Grant(worm: true, backendReady: true);
        var page = EuroOfficeLaunchPage.Render(session, OfficeOrigin, Key, Now);
        StringAssert.Contains(page.Html, "JSON.parse(");
        var bootstrap = OfficeServerEnginePolicy.Create(session.Session, Key,
            immutableRevisionSinkReady: session.ImmutableRevisionSinkReady);
        using var config = JsonDocument.Parse(bootstrap.ConfigurationJson);
        Assert.AreEqual("view", config.RootElement.GetProperty("editorConfig").GetProperty("mode").GetString());
    }

    [TestMethod]
    public void LaunchIdsAreUnpredictableAndMatchTheNativeWebViewPathContract()
    {
        var a = EuroOfficeLaunchId.New();
        var b = EuroOfficeLaunchId.New();
        Assert.IsTrue(EuroOfficeLaunchId.IsValid(a));
        Assert.IsTrue(EuroOfficeLaunchId.IsValid(b));
        Assert.AreEqual(32, a.Length);
        Assert.AreNotEqual(a, b);
        Assert.IsFalse(EuroOfficeLaunchId.IsValid(a.ToUpperInvariant()));
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("0000000000000000000000000000000")]
    [DataRow("000000000000000000000000000000000")]
    [DataRow("0123456789ABCDEF0123456789ABCDEF")]
    [DataRow("0123456789abcdef0123456789abcdez")]
    [DataRow("../secrets/00000000000000000000000")]
    public void RejectsInvalidOrNoncanonicalLaunchIds(string value)
    {
        Assert.IsFalse(EuroOfficeLaunchId.IsValid(value));
    }

    [TestMethod]
    public async Task ResolverDoesNotContactStorageForAnonymousOrMalformedLaunch()
    {
        var store = new SingleUseStore(Grant());
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.IsNull(await EuroOfficeLaunchResolver.ResolveAsync(
            EuroOfficeLaunchId.New(), anonymous, store, OfficeOrigin, Key, Now,
            CancellationToken.None));
        Assert.IsNull(await EuroOfficeLaunchResolver.ResolveAsync(
            "invalid-id", AuthenticatedUser(), store, OfficeOrigin, Key, Now,
            CancellationToken.None));
        Assert.AreEqual(0, store.Calls);
    }

    [TestMethod]
    public async Task ResolverRetrievesOnlyOnceForTheAuthenticatedEmployee()
    {
        string launchId = EuroOfficeLaunchId.New();
        var store = new SingleUseStore(Grant());
        var user = AuthenticatedUser();
        var first = await EuroOfficeLaunchResolver.ResolveAsync(
            launchId, user, store, OfficeOrigin, Key, Now, CancellationToken.None);
        var replay = await EuroOfficeLaunchResolver.ResolveAsync(
            launchId, user, store, OfficeOrigin, Key, Now, CancellationToken.None);
        Assert.IsNotNull(first);
        Assert.IsNull(replay);
        Assert.AreEqual(2, store.Calls);
    }

    private static ClaimsPrincipal AuthenticatedUser() =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "user-17")
        }, "trusted-enterprise"));

    private sealed class SingleUseStore(EuroOfficeAuthorizedLaunch? initial)
        : ITrustedEuroOfficeLaunchStore
    {
        private EuroOfficeAuthorizedLaunch? _available = initial;
        public int Calls { get; private set; }

        public ValueTask<EuroOfficeAuthorizedLaunch?> ConsumeOnceAsync(
            string launchId, ClaimsPrincipal authenticatedUser, CancellationToken cancellationToken)
        {
            Calls++;
            if (authenticatedUser.FindFirst(ClaimTypes.NameIdentifier)?.Value != "user-17")
                return ValueTask.FromResult<EuroOfficeAuthorizedLaunch?>(null);
            var result = _available;
            _available = null;
            return ValueTask.FromResult(result);
        }
    }

    [TestMethod]
    public async Task DenyAllStoreCannotProvideAnySession()
    {
        ITrustedEuroOfficeLaunchStore store = new DenyAllEuroOfficeLaunchStore();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "employee-17")
        }, "trusted-enterprise"));
        Assert.IsNull(await store.ConsumeOnceAsync(
            "0123456789abcdef0123456789abcdef", user, CancellationToken.None));
    }
}
