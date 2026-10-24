namespace GetDocument.Collaboration;

/// <summary>
/// WOPI launch form data. A trusted server must render a POST form with
/// access_token + access_token_ttl; tokens are NEVER appended to URL/query.
/// Same WOPISrc must be shared by users coauthoring the same document.
/// </summary>
public sealed record WopiLaunchForm(
    Uri EditorUrl, Uri WopiSource, string AccessToken, long AccessTokenTtl);

public static class WopiRealtimeLaunch
{
    public static WopiLaunchForm Build(
        string authenticatedDiscoveryXml, string extension, Uri officeOrigin,
        Uri wopiSource, Uri wopiHostOrigin,
        RealtimeCoauthoringTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        OnlyOfficeUriPolicy.RequireTrustedUrl(wopiSource, wopiHostOrigin);
        string expectedPath = "/wopi/files/" + Uri.EscapeDataString(ticket.DocumentId);
        if (!string.Equals(wopiSource.AbsolutePath, expectedPath, StringComparison.Ordinal))
            throw new UnauthorizedAccessException(
                "WOPISrc does not match the document in the signed room ticket.");
        if (!string.IsNullOrEmpty(wopiSource.Query))
            throw new ArgumentException(
                "WOPISrc must be a stable document endpoint, without a user token.");
        if (string.IsNullOrWhiteSpace(ticket.AccessToken) ||
            ticket.DocumentKey.Length != 64 || ticket.AccessTokenTtl <= 0)
            throw new ArgumentException("No trusted live collaboration ticket.");

        Uri action = ticket.CanEdit
            ? WopiDiscoveryCatalog.SelectEditAction(authenticatedDiscoveryXml, extension, officeOrigin)
            : WopiDiscoveryCatalog.SelectViewAction(authenticatedDiscoveryXml, extension, officeOrigin);

        if (action.Query.Contains("WOPISrc=", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Discovery must not set a WOPISrc on behalf of a user.");
        var baseQuery = action.Query.TrimStart('?');
        string query = (baseQuery.Length == 0 ? "" : baseQuery + "&") +
            "WOPISrc=" + Uri.EscapeDataString(wopiSource.AbsoluteUri);
        var editorUrl = new UriBuilder(action) { Query = query }.Uri;
        OnlyOfficeUriPolicy.RequireTrustedUrl(editorUrl, officeOrigin);
        return new WopiLaunchForm(
            editorUrl, wopiSource, ticket.AccessToken, ticket.AccessTokenTtl);
    }
}
