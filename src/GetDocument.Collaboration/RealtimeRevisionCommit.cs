namespace GetDocument.Collaboration;

/// <summary>
/// Converts an authenticated/authorized WOPI save into an immutable CAS revision.
/// The HTTP host MUST independently validate proof headers, token revocation,
/// session ACL, request size, MIME/container and WORM policy before invoking.
/// This service never appends directly to the legacy original.
/// </summary>
public static class RealtimeRevisionCommit
{
    public static async Task<StoredOfficeRevision> CommitAsync(
        ImmutableOfficeRevisionStore store,
        WopiDocument document,
        RealtimeCoauthoringGrant grant,
        DateTimeOffset now,
        string? coolTimestamp,
        Stream completeEditorOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(completeEditorOutput);
        // Snapshot BEFORE producing the plan. The store's cross-process
        // expected-revision CAS rejects races between this read and the write.
        StoredOfficeRevision before = store.ReadCurrent(document.DocumentId);
        RealtimeCoauthoring.PlanNewRevision(document, grant, now,
            before.LastModifiedUtc, coolTimestamp);
        return await store.AppendAsync(document.DocumentId, before.RevisionId,
            completeEditorOutput, cancellationToken);
    }
}
