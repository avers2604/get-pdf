namespace GetDocument.Collaboration;

/// <summary>
/// Server-side callback session. Issued only by a trusted GUP-2/SSO-backed
/// broker. No user/browser fields may construct this session directly.
/// </summary>
public sealed record EuroOfficeCallbackSession(
    string DocumentKey, Uri TrustedDocumentServerOrigin,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Non-consuming session lookup because Document Server sends multiple
/// progress/force-save callbacks. Production must additionally enforce
/// session revocation, per-callback replay deduplication and GUP-2 CAS.
/// </summary>
public interface ITrustedEuroOfficeCallbackSessionStore
{
    ValueTask<EuroOfficeCallbackSession?> FindAsync(
        string callbackId, CancellationToken cancellationToken);
}

/// <summary>
/// All callbacks are denied until the actual corporate session database
/// and office storage authorization are connected.
/// </summary>
public sealed class DenyAllEuroOfficeCallbackSessionStore :
    ITrustedEuroOfficeCallbackSessionStore
{
    public ValueTask<EuroOfficeCallbackSession?> FindAsync(
        string callbackId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<EuroOfficeCallbackSession?>(null);
}

/// <summary>
/// A positive receipt MUST only be created after GUP-2 confirms a new
/// version through its existing archive/CAS/update_document pipeline.
/// </summary>
public sealed record EuroOfficeCallbackCommitReceipt(
    bool Committed, string? RevisionId = null, string? Sha256 = null);

/// <summary>
/// Never substitute the filesystem prototype store for the real GUP-2
/// immutable revision backend. The default sink always denies.
/// </summary>
public interface ITrustedEuroOfficeCallbackRevisionSink
{
    ValueTask<EuroOfficeCallbackCommitReceipt> CommitAsync(
        EuroOfficeSaveCandidate authenticatedCandidate,
        CancellationToken cancellationToken);
}

public sealed class DenyAllEuroOfficeCallbackRevisionSink :
    ITrustedEuroOfficeCallbackRevisionSink
{
    public ValueTask<EuroOfficeCallbackCommitReceipt> CommitAsync(
        EuroOfficeSaveCandidate authenticatedCandidate,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new EuroOfficeCallbackCommitReceipt(false));
}

/// <summary>
/// No automatic status=success response is allowed when the revision was
/// not durably accepted by the corporate database and append-only archive.
/// </summary>
public static class EuroOfficeCallbackAdmission
{
    // Editor sessions may last a full shift; do not use the five-minute
    // *launch token* TTL for status=2 emitted after the editor closes.
    // Continuous ACL revocation is the responsibility of the corporate
    // session store and the commit-time GUP-2 CAS gate.
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(12);

    public static void RequireCurrent(
        EuroOfficeCallbackSession trustedSession,
        Uri configuredOfficeOrigin,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(trustedSession);
        ArgumentNullException.ThrowIfNull(configuredOfficeOrigin);
        OnlyOfficeUriPolicy.RequireHttpsOrigin(configuredOfficeOrigin);

        if (trustedSession.DocumentKey is not { Length: 64 } key ||
            !key.All(ch => ch is >= 'a' and <= 'f' or >= '0' and <= '9') ||
            trustedSession.ExpiresAtUtc <= now ||
            trustedSession.ExpiresAtUtc - now > MaximumLifetime ||
            trustedSession.TrustedDocumentServerOrigin != configuredOfficeOrigin)
            throw new UnauthorizedAccessException(
                "Callback Euro-Office не связан с действующей доверенной сессией.");
    }

    public static int ResponseError(EuroOfficeCallbackCommitReceipt receipt)
    {
        // A real GUP-2 commit must return a verified, nonzero revision and
        // the SHA of the newly archived working copy. A positive boolean alone
        // is insufficient evidence to tell Document Server it can clear edits.
        if (receipt?.Committed != true)
            return 1;

        string? revisionText = receipt.RevisionId;
        string? digest = receipt.Sha256;
        if (string.IsNullOrEmpty(revisionText) || revisionText.Length > 12 ||
            !revisionText.All(char.IsAsciiDigit) ||
            !int.TryParse(revisionText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int revision) ||
            revision <= 0 ||
            digest is null || digest.Length != 64 ||
            !digest.All(ch => ch is >= 'a' and <= 'f' or >= '0' and <= '9'))
            return 1;
        return 0;
    }
}
