namespace GetDocument.WopiGateway;

/// <summary>
/// Fail-closed selection of WOPI HTTP route families.
/// Realtime endpoints are enabled only after a real enterprise ACL/revision
/// adapter has been registered and tested, not because an env var is set.
/// </summary>
public enum WopiServerMode { ReadOnly, Realtime }

public static class WopiHostMode
{
    public static WopiServerMode Resolve(string? requestedMode, bool trustedAdapterAvailable)
    {
        string mode = (requestedMode ?? "").Trim().ToLowerInvariant();
        if (mode is "" or "readonly" or "read-only")
            return WopiServerMode.ReadOnly;
        if (mode is "realtime" or "live")
        {
            if (!trustedAdapterAvailable)
                throw new InvalidOperationException(
                    "Realtime WOPI запрещён: отсутствует доверенный ACL/SSO + immutable revision sink.");
            return WopiServerMode.Realtime;
        }
        throw new ArgumentException("Неподдерживаемый режим WOPI. Разрешены read-only/live.",
            nameof(requestedMode));
    }
}
