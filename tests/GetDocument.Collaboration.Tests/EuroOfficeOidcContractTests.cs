using System.Security.Claims;
using GetDocument.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

[TestClass]
public sealed class EuroOfficeOidcContractTests
{
    private const string Issuer = "https://sso.intra.get.test/realms/get";
    private const string ClientId = "get-documents-broker";
    private static readonly string ClientSecret = new('X', 40);
    private const string UserUuid = "11111111-2222-4333-8444-555555555555";

    [TestMethod]
    public void DisabledOidcRequiresNeitherIssuerNorSecret()
    {
        Assert.IsNull(EuroOfficeOidcConfiguration.Create(false, null, null, null));
        Assert.IsNull(EuroOfficeOidcConfiguration.Create(false,
            "https://unused.invalid", "other-client", "unused"));
    }

    [TestMethod]
    public void ValidCorporateIssuerAndClientArePinnedExactly()
    {
        var configuration = EuroOfficeOidcConfiguration.Create(
            true, Issuer, ClientId, ClientSecret);
        Assert.IsNotNull(configuration);
        Assert.AreEqual(Issuer, configuration.Issuer);
        Assert.AreEqual(ClientId, configuration.ClientId);
        Assert.AreEqual("gup_user_id", EuroOfficeOidcIdentityMapper.Gup2UserIdClaim);
        Assert.AreEqual("CorporateOidc", EuroOfficeOidcIdentityMapper.Scheme);
        Assert.AreEqual(Issuer + "/.well-known/openid-configuration",
            configuration.MetadataAddress.AbsoluteUri);
        Assert.IsFalse(configuration.ToString()!.Contains(ClientSecret));
    }

    [DataTestMethod]
    [DataRow("http://sso.intra.get.test/realms/get")]
    [DataRow("https://evil@sso.intra.get.test/realms/get")]
    [DataRow("https://sso.intra.get.test/realms/get?prompt=none")]
    [DataRow("https://sso.intra.get.test/realms/get#fragment")]
    [DataRow("https://sso.intra.get.test/realms/%2e%2e/admin")]
    [DataRow("file:///etc/passwd")]
    [DataRow("sso.intra.get.test/realms/get")]
    public void RejectsInsecureOrAmbiguousIssuer(string candidate)
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            EuroOfficeOidcConfiguration.Create(
                true, candidate, ClientId, ClientSecret));
    }

    [TestMethod]
    public void RejectsEmptyShortOrUnsafeClientSecretAndClientId()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            EuroOfficeOidcConfiguration.Create(true, Issuer, ClientId, ""));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            EuroOfficeOidcConfiguration.Create(true, Issuer, ClientId, "short"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            EuroOfficeOidcConfiguration.Create(true, Issuer, "evil client!", ClientSecret));
    }

    private static ClaimsPrincipal IdpIdentity(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "ValidatedIdToken"));

    [TestMethod]
    public void ValidatedOidcSubjectAndSignedGup2UuidMapToRestrictedCorporateIdentity()
    {
        var principal = IdpIdentity(
            new Claim("sub", "keycloak-immutable-subject"),
            new Claim("gup_user_id", UserUuid));
        var mapped = EuroOfficeOidcIdentityMapper.MapAfterTokenValidation(principal);
        Assert.AreEqual("CorporateOidc", mapped.Identity!.AuthenticationType);
        Assert.AreEqual(UserUuid, mapped.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        IReadOnlySet<string> permitted = new HashSet<string> { "CorporateOidc" };
        Assert.IsTrue(EuroOfficeEnterprisePrincipalPolicy.IsApproved(mapped, permitted));
        Assert.IsNull(mapped.FindFirst("sub"));
        Assert.IsNull(mapped.FindFirst("gup_user_id"));
    }

    [TestMethod]
    public void MissingConflictingOrNoncanonicalGupIdIsRejected()
    {
        var missing = IdpIdentity(new Claim("sub", "auth-sub"));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeOidcIdentityMapper.MapAfterTokenValidation(missing));

        var duplicate = IdpIdentity(
            new Claim("sub", "auth-sub"),
            new Claim("gup_user_id", UserUuid),
            new Claim("gup_user_id", UserUuid));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeOidcIdentityMapper.MapAfterTokenValidation(duplicate));

        var spoofed = IdpIdentity(
            new Claim("sub", "auth-sub"),
            new Claim("gup_user_id", UserUuid),
            new Claim(ClaimTypes.NameIdentifier,
                "22222222-2222-4333-8444-555555555555"));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeOidcIdentityMapper.MapAfterTokenValidation(spoofed));

        var uppercase = IdpIdentity(
            new Claim("sub", "auth-sub"),
            new Claim("gup_user_id", UserUuid.ToUpperInvariant()));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeOidcIdentityMapper.MapAfterTokenValidation(uppercase));
    }

    [TestMethod]
    public void AnonymousDuplicateSubjectsAndHeaderOnlyIdentityCannotMap()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "subject"), new Claim("gup_user_id", UserUuid)]));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeOidcIdentityMapper.MapAfterTokenValidation(anonymous));

        var duplicated = IdpIdentity(
            new Claim("sub", "subject1"),
            new Claim("sub", "subject2"),
            new Claim("gup_user_id", UserUuid));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeOidcIdentityMapper.MapAfterTokenValidation(duplicated));

        var headerOnly = IdpIdentity(
            new Claim(ClaimTypes.NameIdentifier, UserUuid),
            new Claim("gup_user_id", UserUuid));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeOidcIdentityMapper.MapAfterTokenValidation(headerOnly));
    }

    [TestMethod]
    public void AuthorizationEndpointMustRemainAtConfiguredIssuerAuthority()
    {
        var config = EuroOfficeOidcConfiguration.Create(
            true, Issuer, ClientId, ClientSecret)!;
        Assert.IsTrue(config.IsTrustedAuthorizationEndpoint(
            new Uri("https://sso.intra.get.test/realms/get/protocol/openid-connect/auth")));
        Assert.IsFalse(config.IsTrustedAuthorizationEndpoint(
            new Uri("https://attacker.invalid/openid-connect/auth")));
        Assert.IsFalse(config.IsTrustedAuthorizationEndpoint(
            new Uri("http://sso.intra.get.test/realms/get/protocol/openid-connect/auth")));
        Assert.IsFalse(config.IsTrustedAuthorizationEndpoint(
            new Uri("https://sso.intra.get.test.attacker.invalid/auth")));
    }
}
