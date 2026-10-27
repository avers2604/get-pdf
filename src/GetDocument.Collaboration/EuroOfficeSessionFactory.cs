namespace GetDocument.Collaboration;

/// <summary>
/// Primary online office editor for GET Documents. The DocEditor JSON format
/// is inherited from the ONLYOFFICE-based SDK, but actual compatibility with
/// a pinned Euro-Office release MUST be established in an isolated test lab.
/// </summary>
public enum OfficeServerEngine
{
    EuroOffice,
    OnlyOfficeLegacy,
    CollaboraWopi
}

/// <summary>
/// Single entry point for NEW online editor sessions. This is only a server-side
/// signed configuration contract: it does not start a Document Server, grant
/// access to corporate files or turn on a WOPI write gateway.
///
/// We intentionally do not silently downgrade to a different vendor/engine:
/// permissions, collaboration and saving semantics differ between engines.
/// </summary>
public static class OfficeServerEnginePolicy
{
    public const OfficeServerEngine Primary = OfficeServerEngine.EuroOffice;

    public static OnlyOfficeEditorBootstrap Create(
        OnlyOfficeSessionOptions options,
        byte[] signingKey,
        OfficeServerEngine engine = Primary,
        bool immutableRevisionSinkReady = false) => engine switch
    {
        OfficeServerEngine.EuroOffice =>
            EuroOfficeSessionFactory.Create(options, signingKey, immutableRevisionSinkReady),
        OfficeServerEngine.OnlyOfficeLegacy =>
            OnlyOfficeSessionFactory.Create(options, signingKey),
        OfficeServerEngine.CollaboraWopi =>
            throw new NotSupportedException(
                "Collabora uses an independent WOPI launch; Docs API fallback is prohibited."),
        _ => throw new NotSupportedException(
            "The requested Office editor is not explicitly supported.")
    };
}

/// <summary>
/// Minimal Euro-Office adapter. Reuses existing, validated Docs API session
/// construction instead of forking the editor engine or duplicating JWT code.
/// Saving remains DISABLED in production until the callback protocol and
/// immutable GUP-2 revision sink pass end-to-end compatibility tests.
/// </summary>
public static class EuroOfficeSessionFactory
{
    public static OnlyOfficeEditorBootstrap Create(
        OnlyOfficeSessionOptions options, byte[] signingKey,
        bool immutableRevisionSinkReady = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Euro-Office callback handling and GUP-2 immutable revisions are not
        // live yet. Until the trusted server explicitly declares readiness,
        // never hand out a writable session, even for a CanEdit user.
        var effectiveOptions = options with
        {
            CanEdit = options.CanEdit && immutableRevisionSinkReady
        };
        return OnlyOfficeSessionFactory.Create(effectiveOptions, signingKey);
    }
}
