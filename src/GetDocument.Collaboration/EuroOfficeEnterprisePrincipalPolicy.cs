using System.Security.Claims;

namespace GetDocument.Collaboration;

/// <summary>
/// GD-25.3. Establishes a strict identity boundary for enterprise SSO.
/// A signed editor config or GUP-2 one-use nonce is NOT an authentication.
/// Identity must come from an ASP.NET validated authentication handler,
/// never from an untrusted X-User / X-Forwarded-User header.
/// </summary>
public static class EuroOfficeEnterprisePrincipalPolicy
{
    public static bool IsApproved(
        ClaimsPrincipal? user, IReadOnlySet<string> approvedSchemes)
    {
        if (user is null || approvedSchemes is null || approvedSchemes.Count == 0)
            return false;
        var identities = user.Identities.Where(identity =>
            identity.IsAuthenticated &&
            !string.IsNullOrWhiteSpace(identity.AuthenticationType) &&
            approvedSchemes.Contains(identity.AuthenticationType!)).ToArray();
        if (identities.Length != 1)
            return false;

        // Reject ambiguous overlapping identities, not merely the first.
        var allIds = user.FindAll(ClaimTypes.NameIdentifier)
            .Select(claim => claim.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (allIds.Length != 1 ||
            !Guid.TryParseExact(allIds[0], "D", out Guid id) ||
            id == Guid.Empty ||
            id.ToString("D") != allIds[0])
            return false;

        var matching = identities[0].FindAll(ClaimTypes.NameIdentifier)
            .Select(claim => claim.Value).Distinct(StringComparer.Ordinal).ToArray();
        return matching.Length == 1 &&
               string.Equals(matching[0], allIds[0], StringComparison.Ordinal);
    }
}
