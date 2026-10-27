using System.Security.Claims;
using GetDocument.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

[TestClass]
public sealed class EuroOfficeCorporateSsoPolicyTests
{
    private const string Uid = "11111111-2222-4333-8444-555555555555";
    private static readonly DateTimeOffset Now =
        new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);

    private static ClaimsPrincipal Principal(
        string subject = Uid, long? issued = null, long? expires = null,
        string auth = "jwt-corporate", params Claim[] additional)
    {
        var claims = new List<Claim>
        {
            new("sub", subject),
            new("iat", (issued ?? Now.AddMinutes(-2).ToUnixTimeSeconds()).ToString(
                System.Globalization.CultureInfo.InvariantCulture)),
            new("exp", (expires ?? Now.AddMinutes(12).ToUnixTimeSeconds()).ToString(
                System.Globalization.CultureInfo.InvariantCulture))
        };
        claims.AddRange(additional);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, auth));
    }

    [TestMethod]
    public void StrictSsoIssuerAndAuthorityMustBeSamePinnedHttpsTenant()
    {
        var setting = EuroOfficeCorporateSsoPolicy.CreateSettings(
            "https://idp.intra.get.test/realms/get",
            "https://idp.intra.get.test/realms/get",
            "get-document-eurooffice");
        Assert.AreEqual("get-document-eurooffice", setting.Audience);
        Assert.AreEqual("https://idp.intra.get.test/realms/get", setting.Issuer);
        Assert.ThrowsExactly<ArgumentException>(() =>
            EuroOfficeCorporateSsoPolicy.CreateSettings(
                "http://idp.intra.get.test/realms/get",
                "http://idp.intra.get.test/realms/get", "get-document-eurooffice"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            EuroOfficeCorporateSsoPolicy.CreateSettings(
                "https://idp.intra.get.test/realms/other",
                "https://idp.intra.get.test/realms/get", "get-document-eurooffice"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            EuroOfficeCorporateSsoPolicy.CreateSettings(
                "https://user:password@idp.intra.get.test/realms/get",
                "https://idp.intra.get.test/realms/get", "get-document-eurooffice"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            EuroOfficeCorporateSsoPolicy.CreateSettings(
                "https://idp.intra.get.test/realms/get#fragment",
                "https://idp.intra.get.test/realms/get", "get-document-eurooffice"));
    }

    [TestMethod]
    public void ValidatesRecentTokenAndCreatesOnlyCanonicalGup2EmployeeIdentifier()
    {
        var principal = Principal(additional: [new Claim("preferred_username", "dispatcher")]);
        EuroOfficeCorporateSsoPolicy.BindVerifiedEmployee(principal, Now);
        Assert.AreEqual(Uid, principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.AreEqual(1, principal.FindAll(ClaimTypes.NameIdentifier).Count());
        // Normal username is not accepted as the immutable GUP-2 identifier.
        Assert.AreNotEqual("dispatcher", principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        EuroOfficeCorporateSsoPolicy.BindVerifiedEmployee(principal, Now);
        Assert.AreEqual(1, principal.FindAll(ClaimTypes.NameIdentifier).Count());
    }

    [TestMethod]
    public void UnauthenticatedOrForeignSubjectCannotBeTranslatedIntoSsoPrincipal()
    {
        var anonymous = Principal(auth: "");
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeCorporateSsoPolicy.BindVerifiedEmployee(anonymous, Now));
        var wrong = Principal(subject: "employee-17");
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeCorporateSsoPolicy.BindVerifiedEmployee(wrong, Now));
        var uppercase = Principal(subject: Uid.ToUpperInvariant());
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeCorporateSsoPolicy.BindVerifiedEmployee(uppercase, Now));
        var empty = Principal(subject: Guid.Empty.ToString("D"));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeCorporateSsoPolicy.BindVerifiedEmployee(empty, Now));
    }

    [TestMethod]
    public void RejectsDuplicateOrConflictingIdentityClaimsBeforeLaunch()
    {
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeCorporateSsoPolicy.BindVerifiedEmployee(
                Principal(additional: [new Claim("sub",
                    "22222222-2222-4333-8444-555555555555")]), Now));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeCorporateSsoPolicy.BindVerifiedEmployee(
                Principal(additional: [new Claim(
                    ClaimTypes.NameIdentifier, "22222222-2222-4333-8444-555555555555")]), Now));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeCorporateSsoPolicy.BindVerifiedEmployee(
                Principal(additional: [
                    new Claim(ClaimTypes.NameIdentifier, Uid),
                    new Claim(ClaimTypes.NameIdentifier, Uid)
                ]), Now));
    }

    [TestMethod]
    public void RejectsOldExpiredFutureDatedOrExcessiveLifetimeJwt()
    {
        void Deny(long iat, long exp) =>
            Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
                EuroOfficeCorporateSsoPolicy.BindVerifiedEmployee(
                    Principal(issued: iat, expires: exp), Now));
        Deny(Now.AddMinutes(-16).ToUnixTimeSeconds(), Now.AddMinutes(-1).ToUnixTimeSeconds());
        Deny(Now.AddMinutes(-1).ToUnixTimeSeconds(), Now.AddSeconds(-31).ToUnixTimeSeconds());
        Deny(Now.AddMinutes(1).ToUnixTimeSeconds(), Now.AddMinutes(8).ToUnixTimeSeconds());
        Deny(Now.AddMinutes(-1).ToUnixTimeSeconds(), Now.AddMinutes(30).ToUnixTimeSeconds());
        Deny(Now.AddSeconds(-1).ToUnixTimeSeconds(), Now.AddSeconds(-1).ToUnixTimeSeconds());
    }

    [TestMethod]
    public void RejectsMissingDuplicateOrMalformedJwtTimestampClaims()
    {
        var missingIat = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", Uid), new Claim("exp",
                Now.AddMinutes(4).ToUnixTimeSeconds().ToString())
        ], "jwt-corporate"));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeCorporateSsoPolicy.BindVerifiedEmployee(missingIat, Now));
        var doubledExpiry = Principal(additional: [new Claim("exp",
            Now.AddMinutes(5).ToUnixTimeSeconds().ToString())]);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeCorporateSsoPolicy.BindVerifiedEmployee(doubledExpiry, Now));
        var junkTimestamp = Principal(additional: [new Claim("iat", "2026-10-27")]);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeCorporateSsoPolicy.BindVerifiedEmployee(junkTimestamp, Now));
    }

    [TestMethod]
    public void WhenSsoIsNotConfiguredLaunchMustNotUseClientSuppliedHeaders()
    {
        Assert.IsFalse(EuroOfficeCorporateSsoPolicy.IsSsoEnabled("0"));
        Assert.IsFalse(EuroOfficeCorporateSsoPolicy.IsSsoEnabled(null));
        Assert.IsFalse(EuroOfficeCorporateSsoPolicy.IsSsoEnabled("true"));
        Assert.IsTrue(EuroOfficeCorporateSsoPolicy.IsSsoEnabled("1"));
    }
}
