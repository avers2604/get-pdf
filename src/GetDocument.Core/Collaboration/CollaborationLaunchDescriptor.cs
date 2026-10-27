using System.Text.RegularExpressions;

namespace GetDocument.Core.Collaboration;

/// <summary>
/// A syntactically restricted editor launch. This is NOT an authentication
/// credential and does not issue WOPI tokens. A trusted GUP-2/SSO backend must
/// authorize the user, mint and consume the one-time launch identifier.
/// The browser navigates to a short-lived broker page, whose server performs
/// the WOPI token POST to the editor. No JWT in a client-visible URL.
/// </summary>
public sealed class CollaborationLaunchDescriptor
{
    private const string LaunchPrefix = "/get-document/editor/launch/";
    private static readonly Regex LaunchPathPattern = new(
        @"\A/get-document/editor/launch/[0-9a-f]{32}\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string DisplayName { get; }
    public Uri LaunchPage { get; }
    public Uri BrokerOrigin { get; }
    public Uri OfficeOrigin { get; }

    private CollaborationLaunchDescriptor(
        string displayName, Uri launchPage, Uri brokerOrigin, Uri officeOrigin)
    {
        DisplayName = displayName;
        LaunchPage = launchPage;
        BrokerOrigin = brokerOrigin;
        OfficeOrigin = officeOrigin;
    }

    public static CollaborationLaunchDescriptor Create(
        string displayName, Uri launchPage, Uri brokerOrigin, Uri officeOrigin)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 180 ||
            displayName.Any(ch => char.IsControl(ch) || char.IsSurrogate(ch)))
            throw new ArgumentException("Недопустимое название вкладки.", nameof(displayName));
        RequireHttpsOrigin(brokerOrigin, nameof(brokerOrigin));
        RequireHttpsOrigin(officeOrigin, nameof(officeOrigin));
        if (SameOrigin(brokerOrigin, officeOrigin))
            throw new ArgumentException("Сервер запуска и Office должны иметь разные доверенные origin.");

        ArgumentNullException.ThrowIfNull(launchPage);
        if (!IsHttpsNavigation(launchPage) ||
            !SameOrigin(launchPage, brokerOrigin) ||
            launchPage.AbsolutePath.Length > 256 ||
            !LaunchPathPattern.IsMatch(launchPage.AbsolutePath) ||
            launchPage.Query.Length > 0 ||
            launchPage.Fragment.Length > 0)
            throw new ArgumentException(
                "Ожидается короткий HTTPS launch URL без секрета и с одноразовым идентификатором.",
                nameof(launchPage));

        return new CollaborationLaunchDescriptor(displayName, launchPage,
            brokerOrigin, officeOrigin);
    }

    /// <summary>
    /// Allows about:blank for native WebView shutdown, the ONE launch page
    /// and trusted HTTPS office-origin navigations. No external windows.
    /// Subresource blocking must additionally be enforced by network ACL.
    /// </summary>
    public bool IsAllowedNavigation(Uri? destination)
    {
        if (destination is null || !destination.IsAbsoluteUri)
            return false;
        if (destination.OriginalString == "about:blank")
            return true;
        if (!IsHttpsNavigation(destination))
            return false;
        if (SameOrigin(destination, BrokerOrigin))
            return destination == LaunchPage;
        if (!SameOrigin(destination, OfficeOrigin))
            return false;

        foreach (string raw in destination.Query.TrimStart('?')
                     .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string param = Uri.UnescapeDataString(raw.Split('=', 2)[0]);
            if (param.Equals("access_token", StringComparison.OrdinalIgnoreCase) ||
                param.Equals("token", StringComparison.OrdinalIgnoreCase) ||
                param.Equals("jwt", StringComparison.OrdinalIgnoreCase) ||
                param.Equals("code", StringComparison.OrdinalIgnoreCase) ||
                param.Equals("authorization", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static bool SameOrigin(Uri first, Uri second) =>
        string.Equals(first.GetLeftPart(UriPartial.Authority),
            second.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    private static bool IsHttpsNavigation(Uri value) =>
        value.IsAbsoluteUri && value.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(value.UserInfo) &&
        value.Fragment.Length == 0;

    private static void RequireHttpsOrigin(Uri? value, string name)
    {
        if (value is null || !IsHttpsNavigation(value) ||
            value.AbsolutePath != "/" || value.Query.Length > 0)
            throw new ArgumentException("Разрешён только явный HTTPS origin.", name);
    }
}
