using GetDocument.Collaboration;

namespace GetDocument.WopiGateway;

/// <summary>
/// GUP-2/AD authorization decision supplied by a TRUSTED internal server.
/// This contains stable document identifiers and identity, NEVER file paths.
/// The authorization service must enforce company ACL, WORM classification,
/// revocation, and the right to edit on every GetPermissionAsync call.
/// </summary>
public sealed record ApprovedOfficeDocument(
    string DocumentId, string SessionRevisionId, string FileName,
    string OwnerId, string UserId, string UserFriendlyName,
    bool CanEdit, bool IsWormOriginal);

public interface IEnterpriseDocumentPolicy
{
    ValueTask<ApprovedOfficeDocument?> GetPermissionAsync(
        string documentId, string sessionRevisionId,
        string userId, CancellationToken cancellationToken);
}

/// <summary>Default ACL provider: never discloses any corporate document.</summary>
public sealed class DenyAllEnterpriseDocumentPolicy : IEnterpriseDocumentPolicy
{
    public ValueTask<ApprovedOfficeDocument?> GetPermissionAsync(
        string documentId, string sessionRevisionId,
        string userId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ApprovedOfficeDocument?>(null);
}

/// <summary>
/// Source-first adapter: reuse ImmutableOfficeRevisionStore from GD-09E and
/// existing WOPI HTTP handler. No second file store or office parser.
/// User-facing requests cannot provide a path or choose a storage directory.
/// Production still needs a corporate transactional revision service.
/// </summary>
public sealed class RevisionBackedWopiDocumentStore : ITrustedRealtimeWopiDocumentStore
{
    private readonly ImmutableOfficeRevisionStore _revisions;
    private readonly IEnterpriseDocumentPolicy _policy;
    private readonly TimeProvider _clock;

    public RevisionBackedWopiDocumentStore(
        ImmutableOfficeRevisionStore revisions,
        IEnterpriseDocumentPolicy policy,
        TimeProvider clock)
    {
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask<WopiRealtimeAuthorizedContent?> ResolveForSessionAsync(
        string documentId, string sessionRevisionId, string userId,
        CancellationToken cancellationToken)
    {
        var permission = await _policy.GetPermissionAsync(
            documentId, sessionRevisionId, userId, cancellationToken);
        if (!Matches(permission, documentId, sessionRevisionId, userId))
            return null;

        // This validates the latest stored bytes and SHA-256 before offering
        // any document for delivery. Never read or change WORM originals.
        StoredOfficeRevision revision = _revisions.ReadCurrent(documentId);
        if (!string.Equals(revision.Extension, Path.GetExtension(permission!.FileName),
                StringComparison.OrdinalIgnoreCase))
            return null;

        var document = new WopiDocument(
            documentId, sessionRevisionId, permission.FileName, permission.OwnerId,
            userId, permission.UserFriendlyName, revision.Length,
            permission.CanEdit, permission.IsWormOriginal);

        return new WopiRealtimeAuthorizedContent(
            document, revision,
            _ => ValueTask.FromResult<Stream>(
                new FileStream(revision.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)));
    }

    public async ValueTask<StoredOfficeRevision> CommitNewRevisionAsync(
        WopiRealtimeAuthorizedContent authorized,
        RealtimeCoauthoringGrant grant,
        string? clientTimestamp,
        Stream fullOfficeDocument,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorized);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(fullOfficeDocument);

        // DO NOT rely solely on ACL read at request start. Permissions may be
        // revoked or WORM status may change while bytes are being uploaded.
        var allowed = await _policy.GetPermissionAsync(
            authorized.Document.DocumentId, grant.RevisionId,
            grant.UserId, cancellationToken);
        if (!Matches(allowed, authorized.Document.DocumentId,
                grant.RevisionId, grant.UserId) ||
            !allowed!.CanEdit || allowed.IsWormOriginal ||
            !grant.CanEdit ||
            !string.Equals(allowed.FileName, authorized.Document.FileName,
                StringComparison.Ordinal) ||
            !string.Equals(allowed.OwnerId, authorized.Document.OwnerId,
                StringComparison.Ordinal) ||
            grant.ExpiresAtUnixSeconds <= _clock.GetUtcNow().ToUnixTimeSeconds())
            throw new UnauthorizedAccessException("Изменение документа запрещено текущей ACL.");

        // CAS is performed under ImmutableOfficeRevisionStore's interprocess
        // lock, with a second expectedRevisionId check before publishing.
        return await RealtimeRevisionCommit.CommitAsync(
            _revisions, authorized.Document, grant, _clock.GetUtcNow(),
            clientTimestamp, fullOfficeDocument, cancellationToken);
    }

    private static bool Matches(
        ApprovedOfficeDocument? permission,
        string doc, string session, string user) =>
        permission is not null &&
        string.Equals(permission.DocumentId, doc, StringComparison.Ordinal) &&
        string.Equals(permission.SessionRevisionId, session, StringComparison.Ordinal) &&
        string.Equals(permission.UserId, user, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(permission.FileName) &&
        !string.IsNullOrWhiteSpace(permission.OwnerId) &&
        !string.IsNullOrWhiteSpace(permission.UserFriendlyName) &&
        permission.FileName.IndexOfAny(['/', '\\']) < 0;
}
