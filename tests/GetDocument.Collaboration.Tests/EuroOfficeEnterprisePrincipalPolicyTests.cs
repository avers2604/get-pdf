using System.Security.Claims;
using GetDocument.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

[TestClass]
public sealed class EuroOfficeEnterprisePrincipalPolicyTests
{
    private const string Employee = "11111111-2222-4333-8444-555555555555";
    private static readonly IReadOnlySet<string> Allowed =
        new HashSet<string>(StringComparer.Ordinal) { "CorporateOidc" };

    private static ClaimsPrincipal Principal(string type, params Claim[] claims) =>
        new(new ClaimsIdentity(claims, type));

    [TestMethod]
    public void ApprovedCorporateSchemeAndCanonicalGup2EmployeePass()
    {
        var sso = Principal("CorporateOidc",
            new Claim(ClaimTypes.NameIdentifier, Employee));
        Assert.IsTrue(EuroOfficeEnterprisePrincipalPolicy.IsApproved(sso, Allowed));
    }

    [TestMethod]
    public void UserIdClaimOnUntrustedHeaderSchemeCannotAuthorize()
    {
        var external = Principal("ForwardedUserHeader",
            new Claim(ClaimTypes.NameIdentifier, Employee));
        Assert.IsFalse(EuroOfficeEnterprisePrincipalPolicy.IsApproved(external, Allowed));
        Assert.IsFalse(EuroOfficeEnterprisePrincipalPolicy.IsApproved(external,
            new HashSet<string>(StringComparer.Ordinal)));
    }

    [TestMethod]
    public void RejectsUnboundAnonymousUuidAndNoncanonicalPrincipal()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Employee)]));
        Assert.IsFalse(EuroOfficeEnterprisePrincipalPolicy.IsApproved(anonymous, Allowed));
        Assert.IsFalse(EuroOfficeEnterprisePrincipalPolicy.IsApproved(
            Principal("CorporateOidc", new Claim(ClaimTypes.NameIdentifier,
                Employee.ToUpperInvariant())), Allowed));
        Assert.IsFalse(EuroOfficeEnterprisePrincipalPolicy.IsApproved(
            Principal("CorporateOidc", new Claim(ClaimTypes.NameIdentifier,
                "not-a-uuid")), Allowed));
    }

    [TestMethod]
    public void DuplicateOrConflictingSsoIdentitiesCannotOpenDocument()
    {
        var principal = Principal("CorporateOidc",
            new Claim(ClaimTypes.NameIdentifier, Employee));
        principal.AddIdentity(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier,
                "22222222-2222-4333-8444-555555555555")], "UntrustedHeader"));
        Assert.IsFalse(EuroOfficeEnterprisePrincipalPolicy.IsApproved(principal, Allowed));
        principal = Principal("CorporateOidc",
            new Claim(ClaimTypes.NameIdentifier, Employee));
        principal.AddIdentity(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Employee)], "CorporateOidc"));
        Assert.IsFalse(EuroOfficeEnterprisePrincipalPolicy.IsApproved(principal, Allowed));
    }
}
